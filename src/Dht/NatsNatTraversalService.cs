using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NATS.Client.Core;
using NATS.Client; // Added for Nuid
using NATS.Client.JetStream;

namespace MonoTorrent.Dht
{
    public class NatsNatTraversalService : IDisposable
    {
        private readonly NatsConnection _natsConnection;
        private readonly string _peerInfoSubject = "p2p.peers"; // Generic subject name
        private readonly string _discoveryRequestSubject = "p2p.discovery.request";
        private readonly UdpClient _udpClient; // For hole punching
        private readonly NodeId _localPeerId; // Need the local peer ID

        private IPEndPoint? _myExternalEndPoint;
        private readonly ConcurrentDictionary<NodeId, IPEndPoint> _discoveredPeers = new ConcurrentDictionary<NodeId, IPEndPoint>();
        private CancellationTokenSource? _cts;
        private Task? _subscriptionTask;

        public IPEndPoint? MyExternalEndPoint => _myExternalEndPoint;

        // Constructor requires NATS connection details and local peer ID
        public NatsNatTraversalService(NatsOpts natsOptions, NodeId localPeerId)
        {
            _natsConnection = new NatsConnection(natsOptions);
            _localPeerId = localPeerId;
            // Initialize UDP client - bind to any available port for sending punches
            _udpClient = new UdpClient(0); // Bind to ephemeral port
        }

        /// <summary>
        /// Initializes the service, connects to NATS, discovers external endpoint,
        /// publishes self info, and starts listening for other peers.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _cts.Token;

            try
            {
                await _natsConnection.ConnectAsync();
                Console.WriteLine("[NATS NAT] Connected to NATS.");

                // 1. Discover own external IP/Port
                _myExternalEndPoint = await DiscoverExternalEndPointAsync(linkedToken);
                if (_myExternalEndPoint == null)
                {
                    Console.WriteLine("[NATS NAT] Failed to discover external endpoint. Cannot proceed.");
                    throw new InvalidOperationException("Failed to discover external endpoint via NATS.");
                }
                Console.WriteLine($"[NATS NAT] Discovered external endpoint: {_myExternalEndPoint}");

                // 2. Start subscribing to peer info subject BEFORE publishing self
                _subscriptionTask = Task.Run(() => SubscribeToPeerInfoAsync(linkedToken), linkedToken);

                // 3. Publish own info periodically (or once, depending on strategy)
                // Let's publish once initially, and maybe periodically later if needed.
                await PublishSelfInfoAsync(linkedToken);

                // 4. Hole punching is initiated as peers are discovered in the subscription loop.

                Console.WriteLine("[NATS NAT] Initialization sequence complete. Listening for peers...");

            }
            catch (OperationCanceledException)
            {
                 Console.WriteLine("[NATS NAT] Initialization cancelled.");
                 throw; // Re-throw cancellation
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NATS NAT] Error during initialization: {ex.Message}");
                // Propagate the exception so the caller knows initialization failed
                throw new InvalidOperationException($"NATS NAT Traversal initialization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Discovers the external IP endpoint using NATS request/reply.
        /// Assumes a service listens on _discoveryRequestSubject and replies with "IP|Port".
        /// </summary>
        private async Task<IPEndPoint?> DiscoverExternalEndPointAsync(CancellationToken token)
        {
            // Generate a unique reply subject using the connection
            // Nuid is part of NATS.Client, ensure using NATS.Client; is present
            var replySubject = _natsConnection.NewInbox (); // Correct way to get reply subject
            var requestTimeout = TimeSpan.FromSeconds(10); // Timeout for the discovery request

            try
            {
                Console.WriteLine($"[NATS NAT] Sending discovery request on '{_discoveryRequestSubject}', expecting reply on '{replySubject}'");

                // Send request and wait for reply
                // NATS.Client.Core handles reply subjects automatically for RequestAsync
                // Use positional argument for payload (usually the second argument)
                // The reply subject is typically handled implicitly by RequestAsync in NATS.Client.Core
                // The reply subject is handled implicitly. Remove the 'timeout' named parameter.
                var reply = await _natsConnection.RequestAsync<byte[], string>(
                    _discoveryRequestSubject, // subject
                    Encoding.UTF8.GetBytes(_localPeerId.ToHex()), // payload
                    // Removed replyTo: replySubject,
                    cancellationToken: token
                    // Removed timeout: requestTimeout - rely on default or use NatsReqOpts if needed
                    );

                Console.WriteLine($"[NATS NAT] Received discovery reply: {reply.Data}");

                // Parse the reply "IPAddress|Port"
                var parts = reply.Data.Split('|');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ipAddress) && ushort.TryParse(parts[1], out var port))
                {
                    // Ensure port is cast to int for IPEndPoint constructor
                    return new IPEndPoint(ipAddress, (int)port);
                }
                else
                {
                    Console.WriteLine($"[NATS NAT] Failed to parse discovery reply: {reply.Data}");
                    return null;
                }
            }
            // Catch more general NATS exceptions and standard TimeoutException
            // Catch more general NATS exceptions and standard TimeoutException
            catch (NatsException ex) { // Catch base NATS exception
                 Console.WriteLine($"[NATS NAT Discovery Error] NATS error: {ex.Message}");
                 // Optionally check ex.Message or ex.GetType() for specifics like no servers
                 return null;
            }
            catch (TimeoutException) { // Catch standard timeout
                Console.WriteLine("[NATS NAT Discovery Error] Request timed out waiting for discovery service reply.");
                return null;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[NATS NAT Discovery Error] Discovery request cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NATS NAT Discovery Error] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Publishes the local peer's discovered external endpoint to NATS.
        /// </summary>
        private async Task PublishSelfInfoAsync(CancellationToken token)
        {
            if (_myExternalEndPoint == null)
            {
                 Console.WriteLine("[NATS NAT] Cannot publish self info, external endpoint not discovered.");
                 return;
            }

            // Format: PeerIdHex|IPAddress|Port
            var peerInfo = $"{_localPeerId.ToHex()}|{_myExternalEndPoint.Address}|{_myExternalEndPoint.Port}";
            var data = Encoding.UTF8.GetBytes(peerInfo);

            try
            {
                await _natsConnection.PublishAsync(_peerInfoSubject, data, cancellationToken: token);
                Console.WriteLine($"[NATS NAT] Published self info: {peerInfo}");
            }
             catch (OperationCanceledException) { /* Ignore */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[NATS NAT] Error publishing self info: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribes to the NATS subject and processes incoming peer information.
        /// </summary>
        private async Task SubscribeToPeerInfoAsync(CancellationToken token)
        {
            Console.WriteLine($"[NATS NAT] Subscribing to {_peerInfoSubject}");
            try
            {
                await foreach (var msg in _natsConnection.SubscribeAsync<byte[]>(_peerInfoSubject, cancellationToken: token))
                {
                    try
                    {
                        var peerInfoString = Encoding.UTF8.GetString(msg.Data);
                        var parts = peerInfoString.Split('|');
                        if (parts.Length == 3)
                        {
                            var peerIdHex = parts[0];
                            var ipString = parts[1];
                            var portString = parts[2];

                            // Ignore messages from self
                            if (_localPeerId.ToHex() == peerIdHex)
                                continue;

                            // Parse the NodeId from the hex string
                            NodeId peerId;
                            try {
                                peerId = NodeId.FromHex(peerIdHex);
                            } catch (Exception ex) {
                                Console.WriteLine($"[NATS NAT] Failed to parse NodeId from hex '{peerIdHex}': {ex.Message}");
                                continue; // Skip this peer if ID is invalid
                            }

                            if (IPAddress.TryParse(ipString, out var ipAddress) && ushort.TryParse(portString, out var port))
                            {
                                var discoveredEndPoint = new IPEndPoint(ipAddress, port);

                                // Use TryAdd. If the peer is already known, we won't re-punch.
                                // Could use AddOrUpdate if peers might change endpoints.
                                if (_discoveredPeers.TryAdd(peerId, discoveredEndPoint))
                                {
                                     Console.WriteLine($"[NATS NAT] Discovered peer: {peerIdHex} at {discoveredEndPoint}");
                                     // Initiate hole punching attempt immediately upon discovery
                                     _ = InitiateHolePunchingAsync(discoveredEndPoint, token);
                                }
                            }
                            else {
                                Console.WriteLine($"[NATS NAT] Failed to parse peer info IP/Port: {peerInfoString}");
                            }
                        }
                         else {
                             Console.WriteLine($"[NATS NAT] Received malformed peer info (parts!=3): {peerInfoString}");
                         }
                    }
                    catch (Exception ex)
                    {
                        // Log error processing a specific message but continue subscription
                        Console.WriteLine($"[NATS NAT] Error processing peer info message: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                 Console.WriteLine("[NATS NAT] Subscription cancelled.");
            }
            catch (Exception ex)
            {
                // Log error that stops the subscription loop
                Console.WriteLine($"[NATS NAT] Subscription error: {ex.Message}");
            }
             finally
            {
                 Console.WriteLine($"[NATS NAT] Subscription to {_peerInfoSubject} ended.");
            }
        }

        /// <summary>
        /// Sends UDP packets to the target endpoint to attempt NAT hole punching.
        /// </summary>
        private async Task InitiateHolePunchingAsync(IPEndPoint targetEndPoint, CancellationToken token)
        {
            if (_myExternalEndPoint == null || _udpClient?.Client?.LocalEndPoint == null)
            {
                Console.WriteLine("[NATS NAT] Cannot initiate hole punch: Missing own external or local UDP endpoint.");
                return;
            }

            Console.WriteLine($"[NATS NAT] Attempting hole punch to {targetEndPoint} from {_udpClient.Client.LocalEndPoint}");
            try
            {
                // Send a few dummy packets to the target's external endpoint
                byte[] punchPacket = Encoding.UTF8.GetBytes($"punch-{_localPeerId.ToHex()}"); // Include sender ID?
                for (int i = 0; i < 3; i++)
                {
                    if (token.IsCancellationRequested) break;
                    // Use SendAsync on the UdpClient directly
                    await _udpClient.SendAsync(punchPacket, punchPacket.Length, targetEndPoint);
                    await Task.Delay(50, token); // Small delay between punches
                }
                 Console.WriteLine($"[NATS NAT] Sent punch packets to {targetEndPoint}");

                 // Optionally: Start listening for incoming UDP packets here if needed for confirmation
            }
            catch (OperationCanceledException) { /* Ignore */ }
            catch (SocketException ex)
            {
                Console.WriteLine($"[NATS NAT] SocketException during hole punch to {targetEndPoint}: {ex.SocketErrorCode} - {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NATS NAT] Error during hole punch to {targetEndPoint}: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                // Do not dispose _cts here if it was created from an external token source.
                // The creator of the original token source is responsible for its disposal.
                // If _cts was created *internally* without linking, then disposing here would be correct.
                // For simplicity in this context, we assume the caller manages the original token's lifetime.
                // _cts?.Dispose(); // Removed disposal
                _udpClient?.Dispose();
                // NatsConnection disposal is async
                _natsConnection?.DisposeAsync().AsTask().Wait(); // Simple synchronous wait
                Console.WriteLine("[NATS NAT] Disposed.");
            }
        }
    }
}
