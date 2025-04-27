using System;
using System.Collections.Concurrent;
using System.Collections.Generic; // Added for IDictionary
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Client; // Added for PeerInfo
using NATS.Client.Core;
using NATS.Client; // Added for Nuid
using NATS.Client.JetStream;
using MonoTorrent.Connections.Dht; // Added for IDhtListener
using MonoTorrent.PortForwarding;  // Added for IPortForwarder
using System.Net.Http; // Added for HttpClient
using System.Text.Json; // Added for JSON parsing
using System.Diagnostics;
using MonoTorrent.BEncoding; // Added for Debug.WriteLine

namespace MonoTorrent.Dht
{
    public class NatsNatTraversalService : IDisposable
    {
        // Enable or disable verbose debug logging for NATS NAT service
        private static readonly bool VerboseLogging = false;
        // Store the DHT listener to derive the DHT port for punching
        private IDhtListener? _dhtListener;
        private readonly NatsConnection _natsConnection;

        // Optional DHT engine for direct node injection
        public IDhtEngine? DhtEngine { get; set; }
        private readonly string _peerInfoSubject = "p2p.peers"; // Generic subject name
        private readonly string _discoveryRequestSubject = "p2p.discovery.request";
        private readonly UdpClient _udpClient; // For hole punching
        private readonly NodeId _localPeerId; // Need the local peer ID
        private readonly string _localPeerBencodedId;

        private IPEndPoint? _myExternalEndPoint;
        private readonly ConcurrentDictionary<NodeId, IPEndPoint> _discoveredPeers = new ConcurrentDictionary<NodeId, IPEndPoint> ();
        private CancellationTokenSource? _cts;
        private Task? _subscriptionTask;
        private Task? _publishingTask; // Added for periodic publishing

        // --- DHT RELAY SUPPORT ---
        // Subject for DHT relay messages (for multi-hop)
        private readonly string _dhtRelaySubject;
        // Handler for incoming DHT relay messages
        private Task? _dhtRelaySubscriptionTask;
        // --- END DHT RELAY SUPPORT ---

        public IPEndPoint? MyExternalEndPoint => _myExternalEndPoint;

        // Constructor requires NATS connection details
        public NatsNatTraversalService (NatsOpts natsOptions, NodeId localPeerId, MonoTorrent.BEncoding.BEncodedString localPeerIdBencoded)
        {
            if (VerboseLogging)
                Console.WriteLine ($"[Console] NatsNatTraversalService constructor: localPeerIdBencoded={localPeerIdBencoded?.ToHex () ?? "null"}");
            _natsConnection = new NatsConnection (natsOptions);
            _localPeerId = localPeerId;
            _localPeerBencodedId = localPeerIdBencoded.ToHex ().Replace ("-", "");
            _udpClient = new UdpClient (0);
            _dhtRelaySubject = $"p2p.dhtrelay.{_localPeerId.ToHex ()}";
        }

        /// <summary>
        /// Initializes the service, connects to NATS, uses the provided listener (if available) for port mapping,
        /// publishes self info, and starts listening for other peers.
        /// </summary>
        public event EventHandler<NatsPeerDiscoveredEventArgs>? PeerDiscovered; // Define event

        // Define the EventArgs class here
        public class NatsPeerDiscoveredEventArgs : EventArgs
        {
            public NodeId NodeId { get; }
            public BEncodedString PeerId { get; } // Added PeerId
            public IPEndPoint EndPoint { get; }
            public PeerInfo Peer { get; } // Add PeerInfo for convenience

            // New: Internal IP and port for LAN-aware logic
            public string? InternalIp { get; set; }
            public ushort InternalPort { get; set; }

            public NatsPeerDiscoveredEventArgs (NodeId nodeId, BEncodedString peerId, IPEndPoint endPoint) // Added peerId parameter
            {
                NodeId = nodeId;
                PeerId = peerId; // Store PeerId
                EndPoint = endPoint;
                // Create PeerInfo from the endpoint and PeerId.
                Peer = new PeerInfo (new Uri ($"ipv4://{endPoint}"), peerId);
            }
        }

        /// <param name="listener">The DHT listener providing the local endpoint. Can be null.</param>
        /// <param name="portForwarder">Optional port forwarder for UPnP/NAT-PMP.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync (IDhtListener? listener, IPortForwarder? portForwarder, CancellationToken token = default)
        {
            // Keep the DHT listener for hole punching port
            _dhtListener = listener;
            _cts = CancellationTokenSource.CreateLinkedTokenSource (token);
            var linkedToken = _cts.Token;
            // NodeId is set in constructor (no check needed)

            try {
                await _natsConnection.ConnectAsync ();
                if (VerboseLogging)
                    Debug.WriteLine ("[NATS NAT] Connected to NATS.");

                // 1. Determine external endpoint
                IPEndPoint? localDhtEndpoint = listener?.LocalEndPoint; // Get endpoint from listener if available
                if (localDhtEndpoint == null) {
                    Debug.WriteLine ("[NATS NAT WARNING] No local DHT listener endpoint provided or listener not running. External IP discovery might be less accurate, and UPnP/NAT-PMP cannot be attempted.");
                }

                IPEndPoint? discoveredEndpoint = null;
                int? mappedExternalPort = null;
                IPAddress? publicIP = null;

                // Try UPnP/NAT-PMP for port mapping if available and we have a local DHT port
                if (portForwarder != null && localDhtEndpoint != null) {
                    if (VerboseLogging)
                        Debug.WriteLine ($"[NATS NAT INFO] Attempting port mapping via UPnP/NAT-PMP for port {localDhtEndpoint.Port}...");
                    var mapping = new Mapping (Protocol.Udp, localDhtEndpoint.Port); // Use the DHT listener port
                    try {
                        await portForwarder.RegisterMappingAsync (mapping);
                        // Check if the mapping was successful (external port might be different)
                        // We assume the Mapping object is updated with the actual external port.
                        if (mapping.PublicPort > 0) {
                            mappedExternalPort = mapping.PublicPort;
                            Debug.WriteLine ($"[NATS NAT INFO] Successfully mapped internal port {mapping.PrivatePort} to external port {mappedExternalPort} via UPnP/NAT-PMP.");
                        } else {
                            Debug.WriteLine ($"[NATS NAT WARNING] Port mapping via UPnP/NAT-PMP succeeded but returned invalid external port ({mapping.PublicPort}).");
                        }
                    } catch (Exception ex) {
                        Debug.WriteLine ($"[NATS NAT WARNING] Port mapping via UPnP/NAT-PMP failed: {ex.Message}");
                    }
                }

                // Try HTTP to get Public IP
                if (VerboseLogging)
                    Debug.WriteLine ("[NATS NAT INFO] Attempting HTTP query for public IP...");
                publicIP = await DiscoverPublicIPAsync (linkedToken);
                if (publicIP != null) {
                    if (VerboseLogging)
                        Debug.WriteLine ($"[NATS NAT INFO] Discovered public IP via HTTP: {publicIP}");
                } else {
                    Debug.WriteLine ("[NATS NAT WARNING] Failed to discover public IP via HTTP.");
                }

                // Combine results if both UPnP mapping and HTTP IP discovery were successful
                if (publicIP != null && mappedExternalPort.HasValue && mappedExternalPort.Value > 0) {
                    discoveredEndpoint = new IPEndPoint (publicIP, mappedExternalPort.Value);
                    if (VerboseLogging)
                        Debug.WriteLine ($"[NATS NAT INFO] Using combined UPnP+HTTP external endpoint: {discoveredEndpoint}");
                }

                // Determine the best external endpoint to use
                // *** MODIFIED LOGIC START ***
                string endpointSource;
                if (localDhtEndpoint != null && !IPAddress.Any.Equals (localDhtEndpoint.Address) && !IPAddress.IPv6Any.Equals (localDhtEndpoint.Address)) {
                    // Prioritize local endpoint if it's specific (not Any) - good for local tests
                    _myExternalEndPoint = localDhtEndpoint;
                    endpointSource = "LocalDHTListener (Specific)";
                } else {
                    // Fallback logic: Prefer UPnP+HTTP, then HTTP+LocalPort, then just LocalPort
                    _myExternalEndPoint = discoveredEndpoint ?? // Prefer combined UPnP/HTTP result
                                          (publicIP != null && localDhtEndpoint != null ? new IPEndPoint (publicIP, localDhtEndpoint.Port) : // Fallback to HTTP IP + Local DHT Port
                                          localDhtEndpoint); // Last resort: use the local DHT endpoint (even if Any)
                    endpointSource = discoveredEndpoint != null ? "UPnP+HTTP" : (publicIP != null ? "HTTP+LocalDHTPort" : "LocalDHTListener (Any/Null)");
                }
                if (VerboseLogging)
                    Debug.WriteLine ($"[NATS NAT INFO] Using endpoint for NATS publishing: {_myExternalEndPoint} (Source: {endpointSource})");
                // *** MODIFIED LOGIC END ***

                // 2. Start subscribing to peer info subject BEFORE publishing self
                _subscriptionTask = Task.Run (() => SubscribeToPeerInfoAsync (linkedToken), linkedToken);

                // --- DHT RELAY: Start listening for relay messages ---
                _dhtRelaySubscriptionTask = Task.Run (() => SubscribeToDhtRelayAsync (linkedToken), linkedToken);

                // 3. Start periodic publishing in the background
                _publishingTask = Task.Run (() => StartPeriodicPublishingAsync (linkedToken), linkedToken);

                // 4. Hole punching is initiated as peers are discovered in the subscription loop.

                if (VerboseLogging)
                    Debug.WriteLine ("[NATS NAT] Initialization sequence complete. Listening for peers...");

            } catch (OperationCanceledException) {
                Debug.WriteLine ("[NATS NAT] Initialization cancelled.");
                throw; // Re-throw cancellation
            } catch (Exception ex) {
                Debug.WriteLine ($"[NATS NAT] Error during initialization: {ex.Message}");
                // Propagate the exception so the caller knows initialization failed
                throw new InvalidOperationException ($"NATS NAT Traversal initialization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Discovers the external IP endpoint using NATS request/reply.
        /// Assumes a service listens on _discoveryRequestSubject and replies with "IP|Port".
        /// </summary>
        // Uses api.ipify.org to discover the public IP address
        private async Task<IPAddress?> DiscoverPublicIPAsync (CancellationToken token)
        {
            string ipifyUrl = "https://api.ipify.org?format=json";
            var requestTimeout = TimeSpan.FromSeconds (10); // Timeout for the HTTP request

            // Use a static HttpClient instance if possible, or create one per call if necessary
            // For simplicity here, creating one per call. Consider HttpClientFactory for production.
            using var httpClient = new HttpClient ();
            httpClient.Timeout = requestTimeout;

            try {
                Debug.WriteLine ($"[HTTP Discovery] Querying {ipifyUrl} for Public IP...");
                HttpResponseMessage response = await httpClient.GetAsync (ipifyUrl, token);
                response.EnsureSuccessStatusCode (); // Throw if not 2xx

                string jsonResponse = await response.Content.ReadAsStringAsync ();
                Debug.WriteLine ($"[HTTP Discovery] Received response: {jsonResponse}");

                // Parse the JSON response {"ip":"YOUR_IP_ADDRESS"}
                using (JsonDocument document = JsonDocument.Parse (jsonResponse)) {
                    if (document.RootElement.TryGetProperty ("ip", out JsonElement ipElement) && ipElement.ValueKind == JsonValueKind.String) {
                        string? ipString = ipElement.GetString ();
                        if (IPAddress.TryParse (ipString, out IPAddress? publicIp)) {
                            return publicIp;
                        } else {
                            Debug.WriteLine ($"[HTTP Discovery Error] Failed to parse IP address from JSON: {ipString}");
                        }
                    } else {
                        Debug.WriteLine ("[HTTP Discovery Error] JSON response did not contain expected 'ip' string property.");
                    }
                }
                return null;
            } catch (OperationCanceledException) { Debug.WriteLine ("[HTTP Discovery Error] Request cancelled."); return null; } catch (HttpRequestException ex) { Debug.WriteLine ($"[HTTP Discovery Error] HttpRequestException: {ex.Message}"); return null; } catch (JsonException ex) { Debug.WriteLine ($"[HTTP Discovery Error] JSON parsing error: {ex.Message}"); return null; } catch (Exception ex) { Debug.WriteLine ($"[HTTP Discovery Error] {ex.GetType ().Name}: {ex.Message}"); return null; }
        }
        /// <summary>
        /// Publishes the local peer's discovered external endpoint to NATS.
        /// </summary>
        private async Task PublishSelfInfoAsync (CancellationToken token)
        {
            if (_myExternalEndPoint == null) {
                Debug.WriteLine ("[NATS NAT] Cannot publish self info, external endpoint not discovered.");
                return;
            }

            // Format: NodeId|PeerId|ExternalIP|ExternalPort|InternalIPs(comma-separated)|InternalPort
            string internalIps = GetLocalInternalIpsCsv ();
            int internalPort = (MessageLoopLocalPort () ?? _myExternalEndPoint.Port);
            var peerInfo = $"{_localPeerId.ToHex ()}|{_localPeerBencodedId}|{_myExternalEndPoint.Address}|{_myExternalEndPoint.Port}|{internalIps}|{internalPort}";
            var data = Encoding.UTF8.GetBytes (peerInfo);

            // Helper: Get all local IPv4 addresses as CSV (best effort, cross-platform)
            string GetLocalInternalIpsCsv ()
            {
                try {
                    var ips = new List<string> ();
                    foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces ()) {
                        if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                            continue;
                        foreach (var ua in ni.GetIPProperties ().UnicastAddresses) {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback (ua.Address) &&
                                !ua.Address.ToString ().StartsWith ("169.254.")) // Exclude link-local
                            {
                                ips.Add (ua.Address.ToString ());
                            }
                        }
                    }
                    return string.Join (",", ips);
                } catch {
                    return "";
                }
            }
            // Helper: Try to get the local port from the UDP client (if available)
            int? MessageLoopLocalPort ()
            {
                try {
                    return (_udpClient?.Client?.LocalEndPoint as IPEndPoint)?.Port;
                } catch {
                    return null;
                }
            }

            try {
                await _natsConnection.PublishAsync (_peerInfoSubject, data, cancellationToken: token);
                // Debug.WriteLine($"[NATS NAT] Published self info: {peerInfo}");
            } catch (OperationCanceledException) { /* Ignore */ } catch (Exception ex) {
                Debug.WriteLine ($"[NATS NAT] Error publishing self info: {ex.Message}");
            }
        }

        protected virtual void OnPeerDiscovered (NatsPeerDiscoveredEventArgs e)
        {
            Console.WriteLine ($"[Console] NatsNatTraversalService.OnPeerDiscovered fired for peer {e.Peer.ConnectionUri} NodeId {e.NodeId}");
            PeerDiscovered?.Invoke (this, e);

            // Inject discovered peer into DHT engine if available
            if (DhtEngine != null && DhtEngine.State == DhtState.Ready) {
                try {
                    byte[] compactNode = new byte[26];
                    var nodeIdBytes = e.NodeId.Span;
                    nodeIdBytes.CopyTo (compactNode.AsSpan (0, 20));
                    var ipBytes = e.EndPoint.Address.GetAddressBytes ();
                    if (ipBytes.Length == 4) {
                        ipBytes.CopyTo (compactNode, 20);
                        ushort port = (ushort) e.EndPoint.Port;
                        compactNode[24] = (byte) (port >> 8);
                        compactNode[25] = (byte) (port & 0xFF);
                        DhtEngine.Add (new[] { new ReadOnlyMemory<byte> (compactNode) });
                        Debug.WriteLine ($"[NATS NAT] Injected discovered peer {e.EndPoint} into DHT engine.");
                    }
                } catch (Exception ex) {
                    Debug.WriteLine ($"[NATS NAT] Failed to inject peer into DHT: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Subscribes to the NATS subject and processes incoming peer information.
        /// </summary>
        private async Task SubscribeToPeerInfoAsync (CancellationToken token)
        {
            if (VerboseLogging)
                Debug.WriteLine ($"[NATS NAT] Subscribing to {_peerInfoSubject}");
            try {
                await foreach (var msg in _natsConnection.SubscribeAsync<byte[]> (_peerInfoSubject, cancellationToken: token)) {
                    try {
                        var peerInfoString = Encoding.UTF8.GetString (msg.Data);
                        var parts = peerInfoString.Split ('|');
                        // Format: NodeId|PeerId|ExternalIP|ExternalPort|InternalIPs|InternalPort
                        if (parts.Length >= 6) {
                            var nodeIdHex = parts[0];
                            var peerIdHex = parts[1];
                            var ip = parts[2];
                            var port = parts[3];
                            var internalIpsCsv = parts[4];
                            var internalPortString = parts[5];
                            Console.WriteLine ($"[Console] NATS received NodeId: {nodeIdHex}");
                            Console.WriteLine ($"[Console] NATS received PeerId: {peerIdHex}");

                            // Ignore messages from self by comparing BEncoded PeerIds
                            if (_localPeerBencodedId == peerIdHex)
                                continue;

                            // Parse the NodeId from the hex string
                            NodeId peerNodeId;
                            try {
                                peerNodeId = NodeId.FromHex (nodeIdHex);
                            } catch (Exception ex) {
                                Debug.WriteLine ($"[NATS NAT] Failed to parse NodeId from hex '{nodeIdHex}': {ex.Message}");
                                continue; // Skip this peer if ID is invalid
                            }

                            // Parse the PeerId from the hex string
                            BEncodedString peerId;
                            try {
                                byte[] peerIdBytes = ConvertHexStringToByteArray (peerIdHex);
                                peerId = new BEncodedString (peerIdBytes);
                            } catch (Exception ex) {
                                Debug.WriteLine ($"[NATS NAT] Failed to parse PeerId from hex '{peerIdHex}': {ex.Message}");
                                continue; // Skip this peer if ID is invalid
                            }

                            // Parse discovered peer endpoint
                            if (IPAddress.TryParse (ip, out var discoveredIp) && int.TryParse (port, out var discoveredPort)) {
                                // Determine if discovered IP is local (loopback or local interface)
                                bool isLocal = IPAddress.IsLoopback (discoveredIp);
                                if (!isLocal) {
                                    // Check against all local network interfaces
                                    try {
                                        var localAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces ()
                                            .Where (nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                                            .SelectMany (nic => nic.GetIPProperties ().UnicastAddresses)
                                            .Select (ua => ua.Address)
                                            .ToList ();
                                        if (localAddresses.Any (addr => addr.Equals (discoveredIp)))
                                            isLocal = true;
                                    } catch (Exception ex) {
                                        Debug.WriteLine ($"[NATS NAT] Error checking local interfaces: {ex.Message}");
                                    }
                                }

                                // Only inject as loopback if truly local (loopback or matches one of our local interfaces)
                                IPEndPoint discoveredEndPoint;
                                if (isLocal) {
                                    discoveredEndPoint = new IPEndPoint (discoveredIp, discoveredPort); // Use actual IP, not loopback
                                    Debug.WriteLine ($"[NATS NAT] Treating discovered peer {discoveredIp}:{discoveredPort} as LOCAL (actual local IP injection).");
                                } else {
                                    discoveredEndPoint = new IPEndPoint (discoveredIp, discoveredPort);
                                    Debug.WriteLine ($"[NATS NAT] Treating discovered peer {discoveredIp}:{discoveredPort} as REMOTE (external endpoint). Injecting as remote.");
                                }

                                // Store discovered peer
                                _discoveredPeers.AddOrUpdate (peerNodeId,
                                    discoveredEndPoint,
                                    (key, oldVal) => discoveredEndPoint);

                                // Raise event for discovered peer
                                var eventArgs = new NatsPeerDiscoveredEventArgs (peerNodeId, peerId, discoveredEndPoint) {
                                    InternalIp = internalIpsCsv.Split (new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault (),
                                    InternalPort = ushort.TryParse (internalPortString, out var internalPort) ? internalPort : (ushort) 0
                                };
                                OnPeerDiscovered (eventArgs);

                                // Initiate hole punching attempts (external and internal) immediately upon discovery
                                _ = InitiateHolePunchingAsync (discoveredEndPoint, token);
                                // If internal LAN endpoint provided, try punching it as well
                                if (!string.IsNullOrEmpty (eventArgs.InternalIp) && eventArgs.InternalPort != 0) {
                                    if (IPAddress.TryParse (eventArgs.InternalIp, out var internalIp)) {
                                        var internalEndPoint = new IPEndPoint (internalIp, eventArgs.InternalPort);
                                        _ = InitiateHolePunchingAsync (internalEndPoint, token);
                                    }
                                }
                            } else {
                                Debug.WriteLine ($"[NATS NAT] Failed to parse peer info IP/Port: {peerInfoString}");
                            }
                        } else {
                            Debug.WriteLine ($"[NATS NAT] Received malformed peer info (parts!=3): {peerInfoString}");
                        }
                    } catch (Exception ex) {
                        // Log error processing a specific message but continue subscription
                        Debug.WriteLine ($"[NATS NAT] Error processing peer info message: {ex.Message}");
                    }
                }
            } catch (OperationCanceledException) {
                if (VerboseLogging)
                    Debug.WriteLine ("[NATS NAT] Subscription cancelled.");
            } catch (Exception ex) {
                // Log error that stops the subscription loop
                Debug.WriteLine ($"[NATS NAT] Subscription error: {ex.Message}");
            } finally {
                Debug.WriteLine ($"[NATS NAT] Subscription to {_peerInfoSubject} ended.");
            }
        }

        // --- DHT RELAY SUBSCRIPTION ---
        // Listen for DHT relay messages and inject them into the DHT engine
        private async Task SubscribeToDhtRelayAsync (CancellationToken token)
        {
            if (VerboseLogging)
                Debug.WriteLine ($"[NATS NAT] Subscribing to DHT relay subject {_dhtRelaySubject}");
            try {
                await foreach (var msg in _natsConnection.SubscribeAsync<byte[]> (_dhtRelaySubject, cancellationToken: token)) {
                    try {
                        // Relay message format: [fromNodeId(20 bytes)] + [toNodeId(20 bytes)] + [payload]
                        var data = msg.Data;
                        if (data.Length < 40)
                            continue;
                        var fromNodeId = new NodeId (new ReadOnlySpan<byte> (data, 0, 20).ToArray ());
                        var toNodeId = new NodeId (new ReadOnlySpan<byte> (data, 20, 20).ToArray ());
                        var payload = new byte[data.Length - 40];
                        Array.Copy (data, 40, payload, 0, payload.Length);

                        // Only process if we are the intended recipient
                        if (!_localPeerId.Equals (toNodeId))
                            continue;

                        Debug.WriteLine ($"[NATS NAT] Received DHT relay message from {fromNodeId.ToHex ()} to {toNodeId.ToHex ()} (len={payload.Length})");

                        // Inject the payload into the DHT engine (if available)
                        if (DhtEngine is MonoTorrent.Dht.DhtEngine concreteDhtEngine) {
                            // Inject the relay message into the DHT engine for processing
                            concreteDhtEngine.InjectMessageFromRelay (payload, fromNodeId);
                            Debug.WriteLine ($"[NATS NAT] Injected DHT relay message into DHT engine from {fromNodeId.ToHex ()}.");
                        }
                    } catch (Exception ex) {
                        Debug.WriteLine ($"[NATS NAT] Error processing DHT relay message: {ex.Message}");
                    }
                }
            } catch (OperationCanceledException) {
                Debug.WriteLine ("[NATS NAT] DHT relay subscription cancelled.");
            } catch (Exception ex) {
                Debug.WriteLine ($"[NATS NAT] DHT relay subscription error: {ex.Message}");
            } finally {
                Debug.WriteLine ($"[NATS NAT] DHT relay subscription to {_dhtRelaySubject} ended.");
            }
        }

        // --- DHT RELAY SEND ---
        /// <summary>
        /// Send a DHT message to a target peer via relay (NATS).
        /// </summary>
        public async Task SendDhtRelayAsync (NodeId toNodeId, byte[] dhtPayload)
        {
            // Compose relay message: [fromNodeId(20 bytes)] + [toNodeId(20 bytes)] + [payload]
            byte[] fromBytes = _localPeerId.Span.ToArray ();
            byte[] toBytes = toNodeId.Span.ToArray ();
            byte[] relayMsg = new byte[fromBytes.Length + toBytes.Length + dhtPayload.Length];
            Buffer.BlockCopy (fromBytes, 0, relayMsg, 0, fromBytes.Length);
            Buffer.BlockCopy (toBytes, 0, relayMsg, fromBytes.Length, toBytes.Length);
            Buffer.BlockCopy (dhtPayload, 0, relayMsg, fromBytes.Length + toBytes.Length, dhtPayload.Length);
            string relaySubject = $"p2p.dhtrelay.{toNodeId.ToHex ()}";
            await _natsConnection.PublishAsync (relaySubject, relayMsg);
        }

        /// <summary>
        /// Sends UDP packets to the target endpoint to attempt NAT hole punching.
        /// </summary>
        private async Task InitiateHolePunchingAsync (IPEndPoint targetEndPoint, CancellationToken token)
        {
            // Determine the local DHT port to bind for punching
            int localPort = 0;
            try {
                if (_dhtListener?.LocalEndPoint != null)
                    localPort = _dhtListener.LocalEndPoint.Port;
                else if (_udpClient?.Client?.LocalEndPoint is IPEndPoint ep)
                    localPort = ep.Port;
            } catch { }
            if (_myExternalEndPoint == null || localPort == 0) {
                Debug.WriteLine ("[NATS NAT] Cannot initiate hole punch: Missing external endpoint or unable to determine local DHT port.");
                return;
            }

            // Prepare endpoints: external and internal if available
            var endpoints = new List<IPEndPoint> { targetEndPoint };
            // Use discovered internal IP for LAN punching
            if (_discoveredPeers.TryGetValue (DhtEngine is MonoTorrent.Dht.DhtEngine dhtEng ? dhtEng.LocalId : default, out _)) { /*no-op*/ }
            // Actually use provided InternalIp/InternalPort from event args in subscription
            // But targetEndPoint is external. Pass only external here; internal pulse is handled in subscription

            // Bind a new UDP client on the DHT port to create NAT mapping
            using var punchClient = new UdpClient (new IPEndPoint (IPAddress.Any, localPort));
            if (VerboseLogging)
                Debug.WriteLine ($"[NATS NAT] Hole punching from local port {localPort}");
            try {
                byte[] punchPacket = Encoding.UTF8.GetBytes ($"punch-{_localPeerId.ToHex ()}");
                foreach (var ep in endpoints) {
                    for (int i = 0; i < 3; i++) {
                        if (token.IsCancellationRequested)
                            break;
                        await punchClient.SendAsync (punchPacket, punchPacket.Length, ep);
                        await Task.Delay (50, token);
                    }
                    if (VerboseLogging)
                        Debug.WriteLine ($"[NATS NAT] Hole punch packets sent to {ep}");
                }
            } catch (OperationCanceledException) { /* Ignore */ } catch (SocketException ex) {
                Debug.WriteLine ($"[NATS NAT] SocketException during hole punch to {targetEndPoint}: {ex.SocketErrorCode} - {ex.Message}");
            } catch (Exception ex) {
                Debug.WriteLine ($"[NATS NAT] Error during hole punch to {targetEndPoint}: {ex.Message}");
            }
        }

        /// <summary>
        /// Periodically publishes self info to NATS.
        /// </summary>
        private async Task StartPeriodicPublishingAsync (CancellationToken token)
        {
            var publishInterval = TimeSpan.FromSeconds (3); // Publish every 3 seconds
            if (VerboseLogging)
                Debug.WriteLine ($"[NATS NAT] Starting periodic publishing every {publishInterval.TotalSeconds} seconds.");
            try {
                while (!token.IsCancellationRequested) {
                    await PublishSelfInfoAsync (token);
                    await Task.Delay (publishInterval, token);
                }
            } catch (OperationCanceledException) {
                Debug.WriteLine ("[NATS NAT] Periodic publishing cancelled.");
            } catch (Exception ex) {
                Debug.WriteLine ($"[NATS NAT] Error in periodic publishing loop: {ex.Message}");
            } finally {
                Debug.WriteLine ("[NATS NAT] Periodic publishing stopped.");
            }
        }


        /// <summary>
        /// Returns a snapshot of the peers discovered via NATS subscription.
        /// </summary>
        public IDictionary<NodeId, IPEndPoint> GetDiscoveredPeers () // Changed return type
        {
            // Return a copy to prevent external modification
            return new Dictionary<NodeId, IPEndPoint> (_discoveredPeers); // Return as Dictionary
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose ()
        {
            Dispose (true);
            GC.SuppressFinalize (this);
        }

        protected virtual void Dispose (bool disposing)
        {
            if (disposing) {
                _cts?.Cancel ();
                // Do not dispose _cts here if it was created from an external token source.
                // The creator of the original token source is responsible for its disposal.
                // If _cts was created *internally* without linking, then disposing here would be correct.
                // For simplicity in this context, we assume the caller manages the original token's lifetime.
                // _cts?.Dispose(); // Removed disposal
                _udpClient?.Dispose ();
                // NatsConnection disposal is async
                _natsConnection?.DisposeAsync ().AsTask ().Wait (); // Simple synchronous wait
                Debug.WriteLine ("[NATS NAT] Disposed.");
            }
        }

        // Helper method to convert hex string to byte array
        private static byte[] ConvertHexStringToByteArray (string hexString)
        {
            if (hexString.Length % 2 != 0)
                throw new ArgumentException ("Hex string must have an even number of characters.", nameof (hexString));

            byte[] bytes = new byte[hexString.Length / 2];
            for (int i = 0; i < bytes.Length; i++) {
                string hexPair = hexString.Substring (i * 2, 2);
                bytes[i] = Convert.ToByte (hexPair, 16);
            }
            return bytes;
        }

    }
}
