//
// DhtEngine.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography; // Keep this
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht.Messages;
using MonoTorrent.Dht.Tasks;
using MonoTorrent.PortForwarding; // Added for IPortForwarder

namespace MonoTorrent.Dht
{
    enum ErrorCode
    {
        GenericError = 201,
        ServerError = 202,
        ProtocolError = 203,// malformed packet, invalid arguments, or bad token
        MethodUnknown = 204//Method Unknown
    }

    class TransferMonitor : ITransferMonitor
    {
        long ITransferMonitor.UploadRate => SendMonitor.Rate;
        long ITransferMonitor.DownloadRate => ReceiveMonitor.Rate;
        long ITransferMonitor.BytesSent => SendMonitor.Total;
        long ITransferMonitor.BytesReceived => ReceiveMonitor.Total;

        internal SpeedMonitor SendMonitor { get; } = new SpeedMonitor ();
        internal SpeedMonitor ReceiveMonitor { get; } = new SpeedMonitor ();
    }

    public class DhtEngine : IDisposable, IDhtEngine
    {
        internal static readonly IList<string> DefaultBootstrapRouters = Array.AsReadOnly (new[] {
            "router.bittorrent.com",
            "router.utorrent.com",
            "dht.transmissionbt.com"
        });

        static readonly TimeSpan DefaultAnnounceInternal = TimeSpan.FromMinutes (10);
        static readonly TimeSpan DefaultMinimumAnnounceInterval = TimeSpan.FromMinutes (3);

        // Static registry for active local engines
        static readonly ConcurrentDictionary<NodeId, WeakReference<DhtEngine>> ActiveEngines = new ConcurrentDictionary<NodeId, WeakReference<DhtEngine>>();


        #region Events

        public event EventHandler<PeersFoundEventArgs>? PeersFound;
        public event EventHandler? StateChanged;

        #endregion Events

        internal static MainLoop MainLoop { get; } = new MainLoop ("DhtLoop");

        // IPV6 - create an IPV4 and an IPV6 dht engine
        public AddressFamily AddressFamily { get; private set; } = AddressFamily.InterNetwork;

        public TimeSpan AnnounceInterval => DefaultAnnounceInternal;

        public bool Disposed { get; private set; }

        public ITransferMonitor Monitor { get; }

        public TimeSpan MinimumAnnounceInterval => DefaultMinimumAnnounceInterval;

        public DhtState State { get; private set; }

        /// <summary>
        /// The external IPAddress/Port combination which the DHT is bound to. This is discovered
        /// automatically using the NatsNatTraversalService, if provided.
        /// </summary>
        public System.Net.IPEndPoint? ExternalEndPoint { get; set; }

        internal TimeSpan BucketRefreshTimeout { get; set; }
        public NodeId LocalId => RoutingTable.LocalNodeId; // Made public to match IDhtEngine
        internal MessageLoop MessageLoop { get; }
        public int NodeCount => RoutingTable.CountNodes ();
        IEnumerable<Node> PendingNodes { get; set; }
        internal RoutingTable RoutingTable { get; }
        internal TokenManager TokenManager { get; }
        internal Dictionary<NodeId, List<Node>> Torrents { get; }
        // Proper storage for Get/Put requests (BEP44)
        internal Dictionary<NodeId, StoredDhtItem> LocalStorage { get; }
        public Dictionary<NodeId, StoredDhtItem> LocalStorageProperty => LocalStorage;


        public DhtEngine ()
        {
            var monitor = new TransferMonitor ();
            BucketRefreshTimeout = TimeSpan.FromMinutes (15);
            MessageLoop = new MessageLoop (this, monitor);
            Monitor = monitor;
            PendingNodes = Array.Empty<Node> ();
            RoutingTable = new RoutingTable ();
            State = DhtState.NotReady;
            TokenManager = new TokenManager ();
            Torrents = new Dictionary<NodeId, List<Node>> ();

            LocalStorage = new Dictionary<NodeId, StoredDhtItem>();

            MainLoop.QueueTimeout (TimeSpan.FromMinutes (5), () => {
                if (!Disposed)
                    TokenManager.RefreshTokens ();
                return !Disposed;
            });
        }

        public async void Add (IEnumerable<ReadOnlyMemory<byte>> nodes)
        {
            if (State == DhtState.NotReady) {
                PendingNodes = Node.FromCompactNode (nodes);
            } else {
                // Maybe we should pipeline all our tasks to ensure we don't flood the DHT engine.
                // I don't think it's *bad* that we can run several initialise tasks simultaenously
                // but it might be better to run them sequentially instead. We should also
                // run GetPeers and Announce tasks sequentially.
                foreach (var node in Node.FromCompactNode (nodes)) {
                    try {
                        await Add (node);
                    } catch {
                        // FIXME log this.
                    }
                }
            }
        }
internal async Task Add (Node node)
    => await SendQueryAsync (new Ping (RoutingTable.LocalNodeId), node);

        /// <summary>
        /// Directly adds a node to the routing table without sending a Ping.
        /// Used for scenarios like hairpinning where reachability is assumed.
        /// </summary>
        internal void AddToRoutingTable(Node node)
        {
            DhtEngine.MainLoop.CheckThread(); // Ensure we're on the right thread
            RoutingTable.Add(node);
        }


        public async void Announce (InfoHash infoHash, int port)
        {
            CheckDisposed ();
            if (infoHash is null)
                throw new ArgumentNullException (nameof (infoHash));

            try {
                await MainLoop;
                var task = new AnnounceTask (this, infoHash, port);
                await task.ExecuteAsync ();
            } catch {
                // Ignore?
            }
        }

        void CheckDisposed ()
        {
            if (Disposed)
                throw new ObjectDisposedException (GetType ().Name);
        }

        public void Dispose ()
        {
            if (Disposed)
                return;

            // Unregister from static list first
            ActiveEngines.TryRemove(this.LocalId, out _);

            // Ensure we don't break any threads actively running right now
            MainLoop.QueueWait (() => {
                Disposed = true;
                // Consider stopping the message loop *before* clearing queues in StopAsync?
                // For now, disposal handles cleanup after StopAsync is called.
            });
        }

        public async void GetPeers (InfoHash infoHash)
        {
            CheckDisposed ();
            if (infoHash == null)
                throw new ArgumentNullException (nameof (infoHash));

            try {
                await MainLoop;
                var task = new GetPeersTask (this, infoHash);
                await task.ExecuteAsync ();
            } catch {
                // Ignore?
            }
        }


        /// <summary>
        /// Performs a 'get' operation on the DHT to retrieve a value.
        /// </summary>
        /// <param name="target">The target ID (infohash for immutable, key hash for mutable).</param>
        /// <param name="sequenceNumber">Optional sequence number for mutable gets.</param>
        /// <returns>A tuple containing the value, public key, signature, and sequence number (if available).</returns>
        public async Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetAsync (NodeId target, long? sequenceNumber = null)
        {
            var getStart = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT GET] Start: {getStart:O} Target={target.ToHex()} Seq={sequenceNumber?.ToString() ?? "null"}");
            CheckDisposed ();
            if (target is null)
                throw new ArgumentNullException (nameof (target));

            // First, check local storage
            System.Console.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] GetAsync: Checking local storage for Target {target.ToHex().Substring(0,6)} (Seq: {sequenceNumber?.ToString() ?? "N/A"})");
            if (LocalStorage.TryGetValue(target, out var stored))
            {
                System.Console.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] GetAsync: LocalStorage HIT for Target {target.ToHex().Substring(0,6)}. Returning stored item (Seq: {stored.SequenceNumber?.ToString() ?? "N/A"}).");
                System.Diagnostics.Debug.WriteLine($"[TIMING][DHT GET] LocalStorage HIT (Elapsed: {(DateTime.UtcNow - getStart).TotalMilliseconds:F0} ms)");
                return (stored.Value, stored.PublicKey, stored.Signature, stored.SequenceNumber);
            }
            System.Console.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] GetAsync: LocalStorage MISS for Target {target.ToHex().Substring(0,6)}. Proceeding with network lookup.");

            await MainLoop;
            Console.WriteLine($"[DhtEngine {LocalId}] GetAsync started for Target: {target}, Seq: {sequenceNumber?.ToString() ?? "null"}"); // Log Entry
            // First, find the closest nodes using GetPeersTask logic (or similar)
            var getPeersTask = new GetPeersTask(this, target); // Create the task
            // Execute the task ONCE and get the nodes.
            var nodesToQuery = await getPeersTask.ExecuteAsync();
            Console.WriteLine($"[DhtEngine {LocalId}] GetPeersTask completed for {target}. Found {nodesToQuery.Count()} nodes to query."); // Log nodes found
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT GET] GetPeersTask found {nodesToQuery.Count()} nodes (Elapsed: {(DateTime.UtcNow - getStart).TotalMilliseconds:F0} ms)");

            // 2. Execute the GetTask using the found nodes
            var getTask = new GetTask (this, target, nodesToQuery, sequenceNumber);
            Console.WriteLine($"[DhtEngine {LocalId}] Executing GetTask for {target} with {nodesToQuery.Count()} nodes."); // Log before calling GetTask
            var getTaskStart = DateTime.UtcNow;
            var result = await getTask.ExecuteAsync ();
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT GET] GetTask.ExecuteAsync completed (Elapsed: {(DateTime.UtcNow - getTaskStart).TotalMilliseconds:F0} ms, Total: {(DateTime.UtcNow - getStart).TotalMilliseconds:F0} ms)");
            Console.WriteLine($"[DhtEngine {LocalId}] GetTask completed for {target}. Result: ValuePresent={result.value!=null}, PKPresent={result.publicKey!=null}, SigPresent={result.signature!=null}, Seq={result.sequenceNumber?.ToString() ?? "null"}"); // Log final result
            return result;
        }

        /// <summary>
        /// Performs a 'put' operation to store an immutable item on the DHT.
        /// </summary>
        /// <param name="value">The value to store.</param>
        internal async Task PutImmutableAsync (BEncodedValue value)
        {
            CheckDisposed ();
            if (value is null)
                throw new ArgumentNullException (nameof (value));

            await MainLoop;

            NodeId target;
            using (var sha1 = SHA1.Create())
                target = new NodeId(sha1.ComputeHash(value.Encode()));

            // 1. Find closest nodes using GetPeers logic
            var getPeersTask = new GetPeersTask(this, target);
            var nodes = await getPeersTask.ExecuteAsync(); // Capture the returned nodes

            // 2. Get write tokens from these nodes (using get_peers)
            var nodesWithTokens = new Dictionary<Node, BEncodedString> ();
            var getTokenTasks = new List<Task<SendQueryEventArgs>> ();
            foreach (var node in nodes)
            {
                var getPeers = new GetPeers (LocalId, target);
                getTokenTasks.Add (SendQueryAsync (getPeers, node));
            }
            await Task.WhenAll (getTokenTasks);

            foreach (var task in getTokenTasks)
            {
                var args = task.Result;
                if (!args.TimedOut && args.Response is GetPeersResponse response && response.Token != null)
                {
                    // Find the node this response came from (should match the query target node)
                    var respondingNode = nodes.FirstOrDefault(n => n.Id == response.Id);
                    if (respondingNode != null)
                        nodesWithTokens[respondingNode] = (BEncodedString)response.Token;
                }
            }

            // 3. Send Put requests with tokens
            if (nodesWithTokens.Count > 0)
            {
                var putTask = new PutTask (this, value, nodesWithTokens);
                await putTask.ExecuteAsync ();
            }
            // Else: Log failure to get any tokens?
        }

        /// <summary>
        /// Performs a 'put' operation to store or update a mutable item on the DHT.
        /// </summary>
        /// <param name="publicKey">The public key (32 bytes).</param>
        /// <param name="salt">Optional salt (max 64 bytes).</param>
        /// <param name="value">The value to store.</param>
        /// <param name="sequenceNumber">The sequence number.</param>
        /// <param name="signature">The signature (64 bytes).</param>
        /// <param name="cas">Optional Compare-And-Swap sequence number.</param>
        public async Task PutMutableAsync (BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null)
        {
            var putStart = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT PUT] Start: {putStart:O} PK={publicKey?.ToHex() ?? "null"} Seq={sequenceNumber} Salt={salt?.ToHex() ?? "null"}");
            CheckDisposed ();
            if (publicKey is null || publicKey.Span.Length != 32)
                throw new ArgumentException("Public key must be 32 bytes", nameof(publicKey));
            if (salt != null && salt.Span.Length > 64)
                 throw new ArgumentException("Salt cannot be longer than 64 bytes", nameof(salt));
            if (value is null)
                throw new ArgumentNullException (nameof (value));
            if (signature is null || signature.Span.Length != 64)
                throw new ArgumentException("Signature must be 64 bytes", nameof(signature));

            await MainLoop;

            NodeId target = CalculateMutableTargetId(publicKey, salt);

            // Store locally immediately
            StoreItem(target, new StoredDhtItem(value, publicKey, salt, sequenceNumber, signature));

            // 1. Find closest nodes using GetPeers logic
            var getPeersTask = new GetPeersTask(this, target);
            var nodes = await getPeersTask.ExecuteAsync(); // Capture the returned nodes
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT PUT] GetPeersTask found {nodes.Count()} nodes for target {target.ToHex()} (Elapsed: {(DateTime.UtcNow - putStart).TotalMilliseconds:F0} ms)");

            // 2. Get write tokens from these nodes (using get_peers or get)
            // Using get_peers is simpler as it's already implemented for Announce
            var nodesWithTokens = new Dictionary<Node, BEncodedString> ();
            var getTokenTasks = new List<Task<SendQueryEventArgs>> ();
            foreach (var node in nodes)
            {
                var getPeers = new GetPeers (LocalId, target); // Could use 'get' as well
                getTokenTasks.Add (SendQueryAsync (getPeers, node));
            }
            await Task.WhenAll (getTokenTasks);

            foreach (var task in getTokenTasks)
            {
                var args = task.Result;
                if (!args.TimedOut && args.Response is GetPeersResponse response && response.Token != null)
                {
                    var respondingNode = nodes.FirstOrDefault(n => n.Id == response.Id);
                    if (respondingNode != null)
                        nodesWithTokens[respondingNode] = (BEncodedString)response.Token;
                }
                 // If we used 'get' we'd check for GetResponse
            }
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT PUT] Acquired tokens from {nodesWithTokens.Count} nodes (Elapsed: {(DateTime.UtcNow - putStart).TotalMilliseconds:F0} ms)");

            // 3. Send Put requests with tokens
            if (nodesWithTokens.Count > 0)
            {
                var putTask = new PutTask (this, value, publicKey, salt, sequenceNumber, signature, cas, nodesWithTokens);
                var putTaskStart = DateTime.UtcNow;
                await putTask.ExecuteAsync ();
                System.Diagnostics.Debug.WriteLine($"[TIMING][DHT PUT] PutTask.ExecuteAsync completed (Elapsed: {(DateTime.UtcNow - putTaskStart).TotalMilliseconds:F0} ms, Total: {(DateTime.UtcNow - putStart).TotalMilliseconds:F0} ms)");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TIMING][DHT PUT] No tokens acquired, skipping PutTask (Total Elapsed: {(DateTime.UtcNow - putStart).TotalMilliseconds:F0} ms)");
            }
        }

        /// <summary>
        /// Explicitly store a mutable item in local DHT storage (for tests or manual replication).
        /// </summary>
        public void StoreMutableLocally(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature)
        {
            var target = CalculateMutableTargetId(publicKey, salt);
            StoreItem(target, new StoredDhtItem(value, publicKey, salt, sequenceNumber, signature));
        }

        public static NodeId CalculateMutableTargetId(BEncodedString publicKey, BEncodedString? salt)
        {
            using (var sha1 = SHA1.Create())
            {
                if (salt == null || salt.Span.Length == 0)
                {
                    // Target = SHA1(PublicKey)
                    // Need ToArray for ComputeHash on older frameworks
                    #if NETSTANDARD2_0 || NET472
                    return new NodeId(sha1.ComputeHash(publicKey.Span.ToArray()));
                    #else
                    byte[] hashResult = new byte[20];
                    if (sha1.TryComputeHash(publicKey.Span, hashResult, out int bytesWritten) && bytesWritten == 20)
                        return new NodeId(hashResult);
                    else
                        throw new CryptographicException("Failed to compute SHA1 hash."); // Or handle error appropriately
                    #endif
                }
                else
                {
                    // Target = SHA1(PublicKey + Salt)
                    byte[] combined = new byte[publicKey.Span.Length + salt.Span.Length];
                    publicKey.Span.CopyTo(combined);
                    salt.Span.CopyTo(combined.AsSpan(publicKey.Span.Length));
                    #if NETSTANDARD2_0 || NET472
                    return new NodeId(sha1.ComputeHash(combined));
                    #else
                    byte[] hashResult = new byte[20];
                    if (sha1.TryComputeHash(combined, hashResult, out int bytesWritten) && bytesWritten == 20)
                        return new NodeId(hashResult);
                    else
                        throw new CryptographicException("Failed to compute SHA1 hash."); // Or handle error appropriately
                    #endif
                }
            }
        }

        async Task InitializeAsync (IEnumerable<Node> nodes, string[] bootstrapRouters) // Changed to async Task
        {
            await MainLoop;
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] InitializeAsync started."); // Log start
 
            var initTask = new InitialiseTask (this, nodes, bootstrapRouters);
            try
            {
                await initTask.ExecuteAsync ();
                System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] InitialiseTask completed successfully."); // Log success
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] InitialiseTask FAILED: {ex.Message}\nStackTrace: {ex.StackTrace}"); // Log failure with stack trace
                 // Rethrow or handle as appropriate? For now, just log and let state be set below.
                 // Consider if we should force NotReady state here?
            }
 
            bool needsBootstrap = RoutingTable.NeedsBootstrap; // Check after task execution

            // --- Hairpin/Loopback Handling ---
            // If we started with no nodes/routers AND we are listening on loopback,
            // try adding self. This might help local instances find each other via refresh.
            // Add null check for bootstrapRouters
            if (!nodes.Any() && (bootstrapRouters == null || bootstrapRouters.Length == 0) && MessageLoop.Listener?.LocalEndPoint?.Address?.Equals(IPAddress.Loopback) == true)
            {
                var selfNode = new Node(RoutingTable.LocalNodeId, MessageLoop.Listener.LocalEndPoint);
                RoutingTable.Add(selfNode); // Add self to potentially kickstart discovery
                System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] Added self to routing table for loopback scenario.");
            }
            // --- End Hairpin/Loopback Handling ---

 
            if (needsBootstrap) {
                RaiseStateChanged (DhtState.NotReady);
            } else {
                RaiseStateChanged (DhtState.Ready);
            }
        }

        internal void RaisePeersFound (NodeId infoHash, IList<PeerInfo> peers)
        {
            PeersFound?.Invoke (this, new PeersFoundEventArgs (InfoHash.FromMemory (infoHash.AsMemory ()), peers));
        }

        void RaiseStateChanged (DhtState newState)
        {
            if (State != newState) {
                State = newState;
                StateChanged?.Invoke (this, EventArgs.Empty);
            }
        }

        internal async Task RefreshBuckets ()
        {
            await MainLoop;

            var refreshTasks = new List<Task> ();
            foreach (Bucket b in RoutingTable.Buckets) {
                if (b.LastChanged > BucketRefreshTimeout) {
                    b.Changed ();
                    var task = new RefreshBucketTask (this, b);
                    refreshTasks.Add (task.Execute ());
                }
            }

            if (refreshTasks.Count > 0)
                await Task.WhenAll (refreshTasks).ConfigureAwait (false);
        }

        public async Task<ReadOnlyMemory<byte>> SaveNodesAsync ()
        {
            await MainLoop;

            var details = new BEncodedList ();

            foreach (Bucket b in RoutingTable.Buckets) {
                foreach (Node n in b.Nodes)
                    if (n.State != NodeState.Bad)
                        details.Add (n.CompactNode ());

                if (b.Replacement != null)
                    if (b.Replacement.State != NodeState.Bad)
                        details.Add (b.Replacement.CompactNode ());
            }

            return details.Encode ();
        }

        internal async Task<SendQueryEventArgs> SendQueryAsync (QueryMessage query, Node node)
        {
            var sendStart = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[DHT DEBUG] SendQueryAsync called. Query={query.GetType().Name}, Node={node.EndPoint}, NodeId={node.Id.ToHex()}");
            // Prevent sending queries to self by checking NodeId first, then external endpoint as a fallback.
            bool isSelfNodeId = node.Id.Equals(this.LocalId);
            bool isSelfExternalEndpoint = ExternalEndPoint != null && node.EndPoint.Address.Equals(ExternalEndPoint.Address) && node.EndPoint.Port == ExternalEndPoint.Port;

            if (isSelfNodeId || isSelfExternalEndpoint)
            {
                System.Diagnostics.Debug.WriteLine($"[DHT WARNING] Attempted to send DHT query to self (NodeIdMatch={isSelfNodeId}, EndpointMatch={isSelfExternalEndpoint}) at {node.EndPoint}. Skipping send.");
                // Return default SendQueryEventArgs. The calling code will handle the lack of response.
                // TimedOut will be false, Error will be null, Response will be null.
                return new SendQueryEventArgs(node, node.EndPoint, null);
            }
            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT SENDQUERY] Start: {sendStart:O} Query={query.GetType().Name} Node={node.EndPoint} NodeId={node.Id.ToHex().Substring(0,6)}");
            await MainLoop;

            var e = default (SendQueryEventArgs);
            for (int i = 0; i < 4; i++) {
                var attemptStart = DateTime.UtcNow;
                e = await MessageLoop.SendAsync (query, node);
                System.Diagnostics.Debug.WriteLine($"[TIMING][DHT SENDQUERY] Attempt {i+1} to {node.EndPoint} (Elapsed: {(DateTime.UtcNow - attemptStart).TotalMilliseconds:F0} ms, TimedOut={e.TimedOut})");
                // Extra debug for cross-NAT and token issues
                if (e.Response == null && e.Error == null && e.TimedOut) {
                    System.Diagnostics.Debug.WriteLine($"[DHT DEBUG] Query to {node.EndPoint} (NodeId={node.Id.ToHex().Substring(0,6)}) timed out. QueryType={query.GetType().Name} Attempt={i+1}");
                } else if (e.Response != null) {
                    System.Diagnostics.Debug.WriteLine($"[DHT DEBUG] Query to {node.EndPoint} (NodeId={node.Id.ToHex().Substring(0,6)}) got response: {e.Response.GetType().Name}");
                } else if (e.Error != null) {
                    System.Diagnostics.Debug.WriteLine($"[DHT DEBUG] Query to {node.EndPoint} (NodeId={node.Id.ToHex().Substring(0,6)}) got error: {e.Error}");
                }
                // If the message timed out and we we haven't already hit the maximum retries
                // send again. Otherwise we propagate the eventargs through the Complete event.
                if (e.TimedOut) {
                    node.FailedCount++;
                    continue;
                } else {
                    node.Seen ();
                    System.Diagnostics.Debug.WriteLine($"[TIMING][DHT SENDQUERY] Success to {node.EndPoint} (Total Elapsed: {(DateTime.UtcNow - sendStart).TotalMilliseconds:F0} ms)");
                    return e;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[TIMING][DHT SENDQUERY] All attempts timed out to {node.EndPoint} (Total Elapsed: {(DateTime.UtcNow - sendStart).TotalMilliseconds:F0} ms)");
            return e;
        }

        public Task StartAsync ()
            => StartAsync (ReadOnlyMemory<byte>.Empty);

        public Task StartAsync (ReadOnlyMemory<byte> initialNodes)
            => StartAsync (Node.FromCompactNode (BEncodedString.FromMemory (initialNodes)).Concat (PendingNodes), DefaultBootstrapRouters.ToArray ());

        public Task StartAsync (params string[] bootstrapRouters)
            => StartAsync (Array.Empty<Node> (), bootstrapRouters);
        // Matches IDhtEngine
        public Task StartAsync (NatsNatTraversalService? natsService = null, IPortForwarder? portForwarder = null)
            => StartAsync (ReadOnlyMemory<byte>.Empty, Array.Empty<string>(), natsService, portForwarder); // Delegate to the most specific internal overload
 
        // Matches IDhtEngine
        public Task StartAsync (ReadOnlyMemory<byte> initialNodes, NatsNatTraversalService? natsService = null, IPortForwarder? portForwarder = null)
            => StartAsync (Node.FromCompactNode (BEncodedString.FromMemory (initialNodes)).Concat (PendingNodes), Array.Empty<string> (), natsService, portForwarder); // Use empty array to disable default bootstrap
 
        // This overload is not in IDhtEngine, but is used publicly. Keep it, but ensure it delegates correctly.
        public Task StartAsync (string[] bootstrapRouters, NatsNatTraversalService? natsService = null, IPortForwarder? portForwarder = null)
            => StartAsync (Array.Empty<Node> (), bootstrapRouters, natsService, portForwarder); // Already correct
 // Matches IDhtEngine
 public Task StartAsync (ReadOnlyMemory<byte> initialNodes, string[] bootstrapRouters, NatsNatTraversalService? natsService = null, IPortForwarder? portForwarder = null)
     => StartAsync (Node.FromCompactNode (BEncodedString.FromMemory (initialNodes)).Concat (PendingNodes), bootstrapRouters, natsService, portForwarder); // Already correct

 // Keep the existing internal StartAsync, the public ones delegate to it
 
        // This internal StartAsync now needs the PortForwarder if NATS is used
        async Task StartAsync (IEnumerable<Node> nodes, string[] bootstrapRouters, NatsNatTraversalService? natsService = null, IPortForwarder? portForwarder = null)
        {
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] StartAsync entered.");
            CheckDisposed ();

            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Awaiting MainLoop...");
            await MainLoop;
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] MainLoop awaited.");

            MessageLoop.Start (); // Start listening for messages

            // Determine if this engine is suitable for hairpinning
            bool isLoopback = MessageLoop.Listener?.LocalEndPoint?.Address?.Equals(IPAddress.Loopback) == true;
            // Add null check for bootstrapRouters before accessing Length
            bool isBootstrapping = (bootstrapRouters != null && bootstrapRouters.Length > 0) || nodes.Any();
            bool canHairpin = isLoopback && !isBootstrapping;

            // Register self *before* initializing/bootstrapping
            if (canHairpin)
            {
                ActiveEngines[this.LocalId] = new WeakReference<DhtEngine>(this);
                System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Registered self in ActiveEngines.");
            }

            // Initialize/Bootstrap DHT
            bool needsBootstrapCheck = RoutingTable.NeedsBootstrap;
            int nodeCount = RoutingTable.CountNodes();
            if (isBootstrapping || needsBootstrapCheck || nodeCount == 0) {
                RaiseStateChanged (DhtState.Initialising);
                await InitializeAsync (nodes, bootstrapRouters);
            }

            // Perform hairpin discovery *after* initialization attempt
            if (canHairpin)
            {
                System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Performing hairpin discovery...");
                var selfNode = new Node(this.LocalId, this.MessageLoop.Listener!.LocalEndPoint!); // Listener should be non-null here

                foreach (var kvp in ActiveEngines) // Iterate static dictionary
                {
                    System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Hairpin loop: Checking entry {kvp.Key.ToHex().Substring(0,6)}");
                    // Skip self or disposed engines
                    if (kvp.Key == this.LocalId || !kvp.Value.TryGetTarget(out var otherEngine) || otherEngine.Disposed)
                    {
                        if(kvp.Key == this.LocalId) System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Hairpin loop: Skipping self.");
                        else if (!kvp.Value.TryGetTarget(out _)) System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Hairpin loop: Skipping entry {kvp.Key.ToHex().Substring(0,6)} - WeakReference target lost.");
                        else System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Hairpin loop: Skipping entry {kvp.Key.ToHex().Substring(0,6)} - Other engine disposed.");
                        continue;
                    }

                    // Check if the other engine is also suitable for hairpinning
                    bool otherIsLoopback = otherEngine.MessageLoop.Listener?.LocalEndPoint?.Address?.Equals(IPAddress.Loopback) == true;
                    bool otherListenerValid = otherEngine.MessageLoop.Listener?.LocalEndPoint != null;
                    System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Hairpin loop: Other engine {otherEngine.LocalId.ToHex().Substring(0,6)} - IsLoopback={otherIsLoopback}, ListenerValid={otherListenerValid}");

                    if (otherIsLoopback && otherListenerValid) // Ensure other listener is valid before accessing LocalEndPoint
                    {
                        System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Found other local engine: {otherEngine.LocalId.ToHex().Substring(0,6)} at {otherEngine.MessageLoop.Listener!.LocalEndPoint}");
                        var otherNode = new Node(otherEngine.LocalId, otherEngine.MessageLoop.Listener.LocalEndPoint!);

                        this.AddToRoutingTable(otherNode); // Add other engine to self's routing table
                        otherEngine.AddToRoutingTable(selfNode); // Add self to other engine's routing table
                        // Mark nodes as seen immediately after adding via hairpin
                        // This ensures they are not immediately considered stale/unknown.
                        otherNode.Seen();
                        selfNode.Seen();
                        System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId.ToHex().Substring(0,6)}] Added {otherNode.Id.ToHex().Substring(0,6)} to self's routing table. Added self ({selfNode.Id.ToHex().Substring(0,6)}) to {otherEngine.LocalId.ToHex().Substring(0,6)}'s routing table.");
                    }
                }
            }

            // Set state to Ready regardless of bootstrap success, allowing manual adds/hairpinning
            RaiseStateChanged (DhtState.Ready);

            MainLoop.QueueTimeout (TimeSpan.FromSeconds (30), delegate {
                if (!Disposed) {
                    _ = RefreshBuckets ();
                }
                return !Disposed;
            });
        }

        public async Task StopAsync ()
        {
            await MainLoop;

            MessageLoop.Stop ();
            RaiseStateChanged (DhtState.NotReady);
        }

        internal async Task WaitForState (DhtState state)
        {
            await MainLoop;
            if (State == state)
                return;

            var tcs = new TaskCompletionSource<object> ();

            void handler (object? o, EventArgs e)
            {
                if (State == state) {
                    StateChanged -= handler;
                    tcs.SetResult (true);
                }
            }

            StateChanged += handler;
            await tcs.Task;
        }


        // --- BEP44 Storage Methods ---

        internal bool TryGetStoredItem (NodeId target, out StoredDhtItem? item)
        {
            // TODO: Implement expiration logic based on Timestamp?
            return LocalStorage.TryGetValue (target, out item);
        }

        internal void StoreItem (NodeId target, StoredDhtItem item)
        {
            // Basic store/replace. BEP44 specifies more complex logic:
            // - Check sequence numbers for mutable items (only store if newer or equal with valid CAS).
            // - Potentially limit storage size.
            // - Handle CAS (Compare-And-Swap) logic.

            if (item.IsMutable)
            {
                if (LocalStorage.TryGetValue(target, out var existing) && existing.IsMutable)
                {
                    // Implement sequence number check (BEP44 section 3.2.1)
                    if (item.SequenceNumber <= existing.SequenceNumber)
                    {
                        Console.WriteLine($"[DhtEngine.StoreItem] Discarding store for {target}. Incoming Seq ({item.SequenceNumber}) <= Stored Seq ({existing.SequenceNumber}).");
                        // Optionally, send back an error message (e.g., sequence number too low)?
                        // For now, just silently discard.
                        return;
                    }
                    // TODO: Implement CAS check if item.Cas.HasValue
                }
                Console.WriteLine($"[DhtEngine.StoreItem] Storing mutable item for {target}. Seq: {item.SequenceNumber}");
            }
            else
            {
                Console.WriteLine($"[DhtEngine.StoreItem] Storing immutable item for {target}.");
                // Immutable items just overwrite if they exist
            }
            LocalStorage[target] = item;
        }

        public async Task SetListenerAsync (IDhtListener listener)
        {
            await MainLoop;
            await MessageLoop.SetListener (listener);
        }
    // This method likely needs the PortForwarder too, assuming it's called independently sometimes.
    // If it's *only* ever called from ClientEngine, ClientEngine could handle setting ExternalEndPoint first.
    // Let's assume it needs the PortForwarder for now for completeness.
    public async Task InitializeNatAsync (NatsNatTraversalService natsService, IPortForwarder? portForwarder)
    {
        await MainLoop;
        CheckDisposed ();
        if (natsService == null)
            throw new ArgumentNullException(nameof(natsService));

        try
        {
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] InitializeNatAsync called. Initializing NATS NAT Traversal Service...");
            await natsService.InitializeAsync(MessageLoop.Listener!, portForwarder); // Pass listener and portForwarder
            this.ExternalEndPoint = natsService.MyExternalEndPoint;
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] NATS NAT Traversal Initialized via InitializeNatAsync. External EndPoint: {this.ExternalEndPoint}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DhtEngine {LocalId}] Failed to initialize NATS NAT Traversal Service via InitializeNatAsync: {ex.Message}");
            // Decide how to handle failure - maybe proceed without NAT traversal?
            // For now, just log and continue. DHT might fail if behind NAT.
        }
    }
} // End of DhtEngine class

    // Class to hold stored DHT items (Moved outside DhtEngine class)
    public class StoredDhtItem
    {
        public BEncodedValue Value { get; }
        public BEncodedString? PublicKey { get; } // Null for immutable
        public BEncodedString? Signature { get; } // Null for immutable
        public long? SequenceNumber { get; }    // Null for immutable
        public BEncodedString? Salt { get; }      // Null for immutable or no salt
        public DateTime Timestamp { get; }      // When it was stored
        // public long? Cas { get; } // Optional CAS value from PutRequest

        public bool IsMutable => PublicKey != null;

        // Constructor for mutable
        public StoredDhtItem(BEncodedValue value, BEncodedString pk, BEncodedString? salt, long seq, BEncodedString sig /*, long? cas = null*/)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            PublicKey = pk ?? throw new ArgumentNullException(nameof(pk));
            Salt = salt;
            SequenceNumber = seq;
            Signature = sig ?? throw new ArgumentNullException(nameof(sig));
            // Cas = cas;
            Timestamp = DateTime.UtcNow;
        }

        // Constructor for immutable
        public StoredDhtItem(BEncodedValue value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            Timestamp = DateTime.UtcNow;
        }
    } // End of StoredDhtItem class

    // Moved SetListenerAsync outside DhtEngine class - wait, this doesn't make sense. It needs to be part of DhtEngine.
    // Reverting the move of SetListenerAsync. The syntax errors must be related to the StoredDhtItem placement only.
    // Let's ensure the closing brace for DhtEngine is before StoredDhtItem.

} // End of namespace
