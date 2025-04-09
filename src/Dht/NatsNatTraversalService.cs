using System;
using System.Collections.Concurrent;
using System.Collections.Generic; // Added for IDictionary
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NATS.Client.Core;
using NATS.Client; // Added for Nuid
using NATS.Client.JetStream;
using MonoTorrent.Connections.Dht; // Added for IDhtListener
using MonoTorrent.PortForwarding;  // Added for IPortForwarder
using System.Net.Http; // Added for HttpClient
using System.Text.Json; // Added for JSON parsing

namespace MonoTorrent.Dht
{
    public class NatsNatTraversalService : IDisposable
    {
        private readonly NatsConnection _natsConnection;
        private readonly string _peerInfoSubject = "p2p.peers"; // Generic subject name
        private readonly string _discoveryRequestSubject = "p2p.discovery.request";
        private readonly UdpClient _udpClient; // For hole punching
        private readonly NodeId _localPeerId; // Need the local peer ID
        // Removed _useListenerAsExternalForTesting flag

        private IPEndPoint? _myExternalEndPoint;
        private readonly ConcurrentDictionary<NodeId, IPEndPoint> _discoveredPeers = new ConcurrentDictionary<NodeId, IPEndPoint>();
        private CancellationTokenSource? _cts;
        private Task? _subscriptionTask;
        private Task? _publishingTask; // Added for periodic publishing

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
        /// Initializes the service, connects to NATS, uses the provided listener endpoint,
        /// publishes self info, and starts listening for other peers.
        /// </summary>
        /// <param name="listener">The DHT listener providing the local endpoint.</param>
        /// <param name="portForwarder">Optional port forwarder for UPnP/NAT-PMP.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync(IDhtListener listener, IPortForwarder? portForwarder, CancellationToken token = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _cts.Token;

            try
            {
                await _natsConnection.ConnectAsync();
                Console.WriteLine("[NATS NAT] Connected to NATS.");

                // 1. Determine external endpoint
                if (listener?.LocalEndPoint == null)
                {
                    Console.WriteLine("[NATS NAT ERROR] Provided listener has no LocalEndPoint. Cannot proceed.");
                    throw new InvalidOperationException("Listener must be started and bound before initializing NatsNatTraversalService.");
                }

                IPEndPoint? discoveredEndpoint = null;
                int? mappedExternalPort = null;
                IPAddress? publicIP = null;

                // Try UPnP/NAT-PMP for port mapping if available
                if (portForwarder != null)
                {
                    Console.WriteLine("[NATS NAT INFO] Attempting port mapping via UPnP/NAT-PMP...");
                    var mapping = new Mapping(Protocol.Udp, listener.LocalEndPoint.Port);
                    try
                    {
                        await portForwarder.RegisterMappingAsync(mapping);
                        // Check if the mapping was successful (external port might be different)
                        // We assume the Mapping object is updated with the actual external port.
                        if (mapping.PublicPort > 0) {
                            mappedExternalPort = mapping.PublicPort;
                            Console.WriteLine($"[NATS NAT INFO] Successfully mapped internal port {mapping.PrivatePort} to external port {mappedExternalPort} via UPnP/NAT-PMP.");
                        } else {
                             Console.WriteLine($"[NATS NAT WARNING] Port mapping via UPnP/NAT-PMP succeeded but returned invalid external port ({mapping.PublicPort}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NATS NAT WARNING] Port mapping via UPnP/NAT-PMP failed: {ex.Message}");
                    }
                }

                // Try HTTP to get Public IP
                Console.WriteLine("[NATS NAT INFO] Attempting HTTP query for public IP...");
                publicIP = await DiscoverPublicIPAsync(linkedToken);
                if (publicIP != null) {
                    Console.WriteLine($"[NATS NAT INFO] Discovered public IP via HTTP: {publicIP}");
                } else {
                    Console.WriteLine("[NATS NAT WARNING] Failed to discover public IP via HTTP.");
                }

                // Combine results if both UPnP mapping and HTTP IP discovery were successful
                if (publicIP != null && mappedExternalPort.HasValue && mappedExternalPort.Value > 0)
                {
                    discoveredEndpoint = new IPEndPoint(publicIP, mappedExternalPort.Value);
                    Console.WriteLine($"[NATS NAT INFO] Using combined UPnP+HTTP external endpoint: {discoveredEndpoint}");
                }

                // Fallback to listener endpoint if UPnP+HTTP failed
                _myExternalEndPoint = discoveredEndpoint ?? listener.LocalEndPoint;
                Console.WriteLine($"[NATS NAT INFO] Using endpoint for NATS publishing: {_myExternalEndPoint} (Source: {(discoveredEndpoint != null ? "UPnP+HTTP" : "Listener")})");

                // 2. Start subscribing to peer info subject BEFORE publishing self
                _subscriptionTask = Task.Run(() => SubscribeToPeerInfoAsync(linkedToken), linkedToken);

                // 3. Start periodic publishing in the background
                _publishingTask = Task.Run(() => StartPeriodicPublishingAsync(linkedToken), linkedToken);

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
        // Uses api.ipify.org to discover the public IP address
        private async Task<IPAddress?> DiscoverPublicIPAsync(CancellationToken token)
        {
            string ipifyUrl = "https://api.ipify.org?format=json";
            var requestTimeout = TimeSpan.FromSeconds(10); // Timeout for the HTTP request

            // Use a static HttpClient instance if possible, or create one per call if necessary
            // For simplicity here, creating one per call. Consider HttpClientFactory for production.
            using var httpClient = new HttpClient();
            httpClient.Timeout = requestTimeout;

            try
            {
                Console.WriteLine($"[HTTP Discovery] Querying {ipifyUrl} for Public IP...");
                HttpResponseMessage response = await httpClient.GetAsync(ipifyUrl, token);
                response.EnsureSuccessStatusCode(); // Throw if not 2xx

                string jsonResponse = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[HTTP Discovery] Received response: {jsonResponse}");

                // Parse the JSON response {"ip":"YOUR_IP_ADDRESS"}
                using (JsonDocument document = JsonDocument.Parse(jsonResponse))
                {
                    if (document.RootElement.TryGetProperty("ip", out JsonElement ipElement) && ipElement.ValueKind == JsonValueKind.String)
                    {
                        string? ipString = ipElement.GetString();
                        if (IPAddress.TryParse(ipString, out IPAddress? publicIp))
                        {
                            return publicIp;
                        }
                        else
                        {
                             Console.WriteLine($"[HTTP Discovery Error] Failed to parse IP address from JSON: {ipString}");
                        }
                    }
                    else
                    {
                         Console.WriteLine("[HTTP Discovery Error] JSON response did not contain expected 'ip' string property.");
                    }
                }
                return null;
            }
            catch (OperationCanceledException) { Console.WriteLine("[HTTP Discovery Error] Request cancelled."); return null; }
            catch (HttpRequestException ex) { Console.WriteLine($"[HTTP Discovery Error] HttpRequestException: {ex.Message}"); return null; }
            catch (JsonException ex) { Console.WriteLine($"[HTTP Discovery Error] JSON parsing error: {ex.Message}"); return null; }
            catch (Exception ex) { Console.WriteLine($"[HTTP Discovery Error] {ex.GetType().Name}: {ex.Message}"); return null; }
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
    // GetDiscoveredPeers method moved outside InitiateHolePunchingAsync
    
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
        /// Periodically publishes self info to NATS.
        /// </summary>
        private async Task StartPeriodicPublishingAsync(CancellationToken token)
        {
            var publishInterval = TimeSpan.FromSeconds(3); // Publish every 3 seconds
            Console.WriteLine($"[NATS NAT] Starting periodic publishing every {publishInterval.TotalSeconds} seconds.");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await PublishSelfInfoAsync(token);
                    await Task.Delay(publishInterval, token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[NATS NAT] Periodic publishing cancelled.");
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"[NATS NAT] Error in periodic publishing loop: {ex.Message}");
            }
            finally
            {
                 Console.WriteLine("[NATS NAT] Periodic publishing stopped.");
            }
        }


        /// <summary>
        /// Returns a snapshot of the peers discovered via NATS subscription.
        /// </summary>
        public IDictionary<NodeId, IPEndPoint> GetDiscoveredPeers() // Changed return type
        {
            // Return a copy to prevent external modification
            return new Dictionary<NodeId, IPEndPoint>(_discoveredPeers); // Return as Dictionary
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
