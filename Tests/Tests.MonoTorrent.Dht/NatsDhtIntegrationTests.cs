using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NUnit.Framework;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.PieceWriter;

namespace MonoTorrent.Dht
{
    [TestFixture]
    public class NatsDhtIntegrationTests
    {
        private static readonly string NatsServerUrl = "nats://demo.nats.io:4222";
        private static readonly NatsOpts NatsOptions = NatsOpts.Default with { Url = NatsServerUrl };

        private DhtEngine? dhtEngineA;
        private DhtEngine? dhtEngineB;
        private IDhtListener? listenerA;
        private IDhtListener? listenerB;
        private NatsNatTraversalService? natsServiceA;
        private NatsNatTraversalService? natsServiceB;

        // Removed placeholder discovery service fields


        [SetUp]
        public void Setup()
        {
            dhtEngineA?.Dispose();
            dhtEngineB?.Dispose();
            listenerA?.Stop();
            listenerB?.Stop();
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();
            // Removed placeholder discovery service cleanup

            dhtEngineA = new DhtEngine();
            dhtEngineB = new DhtEngine();
            // Removed placeholder discovery list initialization

            listenerA = new DhtListener(new IPEndPoint(IPAddress.Loopback, 0));
            listenerB = new DhtListener(new IPEndPoint(IPAddress.Loopback, 0));
            dhtEngineA.SetListenerAsync(listenerA).GetAwaiter().GetResult();
            dhtEngineB.SetListenerAsync(listenerB).GetAwaiter().GetResult();

            listenerA.Start();
            listenerB.Start();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((listenerA.Status != ListenerStatus.Listening || listenerB.Status != ListenerStatus.Listening) && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }

            Assert.AreEqual(ListenerStatus.Listening, listenerA.Status, "Listener A did not start listening.");
            Assert.AreEqual(ListenerStatus.Listening, listenerB.Status, "Listener B did not start listening.");
            Assert.IsNotNull(listenerA.LocalEndPoint, "Listener A failed to bind.");
            Assert.IsNotNull(listenerB.LocalEndPoint, "Listener B failed to bind.");

            // Removed placeholder discovery service startup
        }

        [TearDown]
        public async Task Teardown()
        {
            // Removed placeholder discovery service teardown
            listenerA?.Stop();
            listenerB?.Stop();
            dhtEngineA?.Dispose();
            dhtEngineB?.Dispose();
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();
            await Task.Delay(100);
        }

        // Removed placeholder discovery service method StartSimplePlaceholderDiscoveryServiceAsync
        [Test]
        public async Task Test_NatsDiscovery_And_Bootstrap()
        {
            Assert.IsNotNull(dhtEngineA, "DhtEngine A should not be null");
            Assert.IsNotNull(dhtEngineB, "DhtEngine B should not be null");
            Assert.IsNotNull(listenerA?.LocalEndPoint, "Listener A should have a local endpoint");
            Assert.IsNotNull(listenerB?.LocalEndPoint, "Listener B should have a local endpoint");
            // Removed assertion for _localEndpointsForDiscovery

            // Instantiate NATS services normally (no testing flag)
            natsServiceA = new NatsNatTraversalService(NatsOptions, dhtEngineA!.LocalId);
            natsServiceB = new NatsNatTraversalService(NatsOptions, dhtEngineB!.LocalId);

            // Initialize NATS services, passing the listener and null for portForwarder
            var initTaskA = natsServiceA.InitializeAsync(listenerA!, portForwarder: null);
            var initTaskB = natsServiceB.InitializeAsync(listenerB!, portForwarder: null);
            try
            {
                await Task.WhenAll(initTaskA, initTaskB);
                Console.WriteLine("[Test Bootstrap] NATS Services Initialized.");
            }
            catch (Exception ex)
            {
                 Assert.Fail($"NATS Service InitializeAsync failed unexpectedly: {ex.Message}");
                 return;
            }

            // Wait for peers to discover each other via NATS, with a timeout
            Console.WriteLine("[Test Bootstrap] Waiting for NATS peer discovery...");
            var discoveryTimeout = TimeSpan.FromSeconds(20); // Increased timeout
            var discoverySw = System.Diagnostics.Stopwatch.StartNew();
            bool discoveredA = false;
            bool discoveredB = false;
            IDictionary<NodeId, IPEndPoint> discoveredByA = null!;
            IDictionary<NodeId, IPEndPoint> discoveredByB = null!;

            while (discoverySw.Elapsed < discoveryTimeout && (!discoveredA || !discoveredB))
            {
                discoveredByA = natsServiceA.GetDiscoveredPeers();
                discoveredByB = natsServiceB.GetDiscoveredPeers();
                discoveredA = discoveredByA.ContainsKey(dhtEngineB!.LocalId);
                discoveredB = discoveredByB.ContainsKey(dhtEngineA!.LocalId);
                if (!discoveredA || !discoveredB)
                    await Task.Delay(250); // Poll every 250ms
            }
            discoverySw.Stop();
            Console.WriteLine($"[Test Bootstrap] Peer discovery loop finished after {discoverySw.ElapsedMilliseconds}ms. DiscoveredA: {discoveredA}, DiscoveredB: {discoveredB}");

            // Discovered peers retrieved within the loop above

            Console.WriteLine($"[Test Bootstrap] Peers discovered by A: {discoveredByA.Count}");
            foreach(var kvp in discoveredByA) Console.WriteLine($"  - {kvp.Key.ToHex()} @ {kvp.Value}");
            Console.WriteLine($"[Test Bootstrap] Peers discovered by B: {discoveredByB.Count}");
            foreach(var kvp in discoveredByB) Console.WriteLine($"  - {kvp.Key.ToHex()} @ {kvp.Value}");

            // Assert that each service discovered the other
            Assert.IsTrue(discoveredByA.ContainsKey(dhtEngineB!.LocalId), "NATS Service A did not discover Peer B");
            Assert.IsTrue(discoveredByB.ContainsKey(dhtEngineA!.LocalId), "NATS Service B did not discover Peer A");

            // Get the specific endpoints discovered for bootstrapping
            var endpointB_discoveredByA = discoveredByA[dhtEngineB!.LocalId];
            var endpointA_discoveredByB = discoveredByB[dhtEngineA!.LocalId];

            // Create nodes using the *actually discovered* endpoints via NATS p2p.peers subject
            var nodeA_forB = new Node(dhtEngineA.LocalId, endpointA_discoveredByB);
            var nodeB_forA = new Node(dhtEngineB.LocalId, endpointB_discoveredByA);

            var initialNodesA = Node.CompactNode(new[] { nodeB_forA }).AsMemory();
            var initialNodesB = Node.CompactNode(new[] { nodeA_forB }).AsMemory();

            Console.WriteLine($"[Test Bootstrap] Starting DHT Engine A with initial peer B: {nodeB_forA.EndPoint}");
            Console.WriteLine($"[Test Bootstrap] Starting DHT Engine B with initial peer A: {nodeA_forB.EndPoint}");
            // Ensure correct overload is used, Array.Empty<string>() is fine for the second param
            var startTaskA = dhtEngineA.StartAsync(initialNodesA, Array.Empty<string>());
            var startTaskB = dhtEngineB.StartAsync(initialNodesB, Array.Empty<string>());
            await Task.WhenAll(startTaskA, startTaskB);
            Console.WriteLine("[Test Bootstrap] DHT engines started.");

            Console.WriteLine("[Test Bootstrap] Waiting for DHT engines to reach Ready state...");
            var timeout = TimeSpan.FromSeconds(15);
            var readyTaskA = WaitForReadyStateAsync(dhtEngineA, timeout);
            var readyTaskB = WaitForReadyStateAsync(dhtEngineB, timeout);
            await Task.WhenAll(readyTaskA, readyTaskB);

            Assert.AreEqual(DhtState.Ready, dhtEngineA.State, $"EngineA DHT did not reach Ready state within {timeout.TotalSeconds}s");
            Assert.AreEqual(DhtState.Ready, dhtEngineB.State, $"EngineB DHT did not reach Ready state within {timeout.TotalSeconds}s");
            Console.WriteLine ("[Test Bootstrap] DHT engines reached Ready state");
        }

        [Test]
        public async Task Test_NatsDiscovery_Then_DHT_PutGet()
        {
            Assert.IsNotNull(dhtEngineA, "DhtEngine A should not be null");
            Assert.IsNotNull(dhtEngineB, "DhtEngine B should not be null");
            Assert.IsNotNull(listenerA?.LocalEndPoint, "Listener A should have a local endpoint");
            Assert.IsNotNull(listenerB?.LocalEndPoint, "Listener B should have a local endpoint");
            // Removed assertion for _localEndpointsForDiscovery

            // Instantiate NATS services normally
            natsServiceA = new NatsNatTraversalService(NatsOptions, dhtEngineA!.LocalId);
            natsServiceB = new NatsNatTraversalService(NatsOptions, dhtEngineB!.LocalId);
            // Initialize NATS services, passing the listener and null for portForwarder
            var initTaskA_pg = natsServiceA.InitializeAsync(listenerA!, portForwarder: null);
            var initTaskB_pg = natsServiceB.InitializeAsync(listenerB!, portForwarder: null);
             try
            {
                await Task.WhenAll(initTaskA_pg, initTaskB_pg);
                 Console.WriteLine("[Test PutGet] NATS Services Initialized.");
            }
            catch (Exception ex)
            {
                 Assert.Fail($"NATS Service InitializeAsync failed unexpectedly for PutGet: {ex.Message}");
                 return;
            }
           

            // Wait for peers to discover each other via NATS, with a timeout
            Console.WriteLine("[Test PutGet] Waiting for NATS peer discovery...");
            var discoveryTimeout_pg = TimeSpan.FromSeconds(20); // Increased timeout
            var discoverySw_pg = System.Diagnostics.Stopwatch.StartNew();
            bool discoveredA_pg_status = false;
            bool discoveredB_pg_status = false;
            IDictionary<NodeId, IPEndPoint> discoveredByA_pg = null!;
            IDictionary<NodeId, IPEndPoint> discoveredByB_pg = null!;

            while (discoverySw_pg.Elapsed < discoveryTimeout_pg && (!discoveredA_pg_status || !discoveredB_pg_status))
            {
                discoveredByA_pg = natsServiceA.GetDiscoveredPeers();
                discoveredByB_pg = natsServiceB.GetDiscoveredPeers();
                discoveredA_pg_status = discoveredByA_pg.ContainsKey(dhtEngineB!.LocalId);
                discoveredB_pg_status = discoveredByB_pg.ContainsKey(dhtEngineA!.LocalId);
                if (!discoveredA_pg_status || !discoveredB_pg_status)
                    await Task.Delay(250); // Poll every 250ms
            }
            discoverySw_pg.Stop();
            Console.WriteLine($"[Test PutGet] Peer discovery loop finished after {discoverySw_pg.ElapsedMilliseconds}ms. DiscoveredA: {discoveredA_pg_status}, DiscoveredB: {discoveredB_pg_status}");

            // Discovered peers retrieved within the loop above

             Console.WriteLine($"[Test PutGet] Peers discovered by A: {discoveredByA_pg.Count}");
             Console.WriteLine($"[Test PutGet] Peers discovered by B: {discoveredByB_pg.Count}");

            // Assert discovery after the wait loop
            Assert.IsTrue(discoveredA_pg_status, "NATS Service A did not discover Peer B for PutGet within timeout.");
            Assert.IsTrue(discoveredB_pg_status, "NATS Service B did not discover Peer A for PutGet within timeout.");

            var endpointB_discoveredByA_pg = discoveredByA_pg[dhtEngineB!.LocalId];
            var endpointA_discoveredByB_pg = discoveredByB_pg[dhtEngineA!.LocalId];

            dhtEngineA.ExternalEndPoint = natsServiceA.MyExternalEndPoint; // Set for consistency if needed elsewhere
            dhtEngineB.ExternalEndPoint = natsServiceB.MyExternalEndPoint;

            // Create nodes using the discovered endpoints
            var nodeA_forB_pg = new Node(dhtEngineA.LocalId, endpointA_discoveredByB_pg);
            var nodeB_forA_pg = new Node(dhtEngineB.LocalId, endpointB_discoveredByA_pg);

            var initialNodesA = Node.CompactNode(new[] { nodeB_forA_pg }).AsMemory();
            var initialNodesB = Node.CompactNode(new[] { nodeA_forB_pg }).AsMemory();

            Console.WriteLine($"[Test PutGet] Starting DHT Engine A with initial peer B: {nodeB_forA_pg.EndPoint}");
            Console.WriteLine($"[Test PutGet] Starting DHT Engine B with initial peer A: {nodeA_forB_pg.EndPoint}");
            var startTaskA = dhtEngineA.StartAsync(initialNodesA, Array.Empty<string>()); // No NATS service needed here
            var startTaskB = dhtEngineB.StartAsync(initialNodesB, Array.Empty<string>()); // No NATS service needed here
            await Task.WhenAll(startTaskA, startTaskB);
            Console.WriteLine("[Test PutGet] DHT engines started.");

            Console.WriteLine("[Test PutGet] Waiting for DHT engines to reach Ready state...");
            var timeout = TimeSpan.FromSeconds(15);
            var readyTaskA = WaitForReadyStateAsync(dhtEngineA, timeout);
            var readyTaskB = WaitForReadyStateAsync(dhtEngineB, timeout);
            await Task.WhenAll(readyTaskA, readyTaskB);

            Assert.AreEqual(DhtState.Ready, dhtEngineA.State, $"EngineA DHT not Ready for PutGet within {timeout.TotalSeconds}s");
            Assert.AreEqual(DhtState.Ready, dhtEngineB.State, $"EngineB DHT not Ready for PutGet within {timeout.TotalSeconds}s");
            Console.WriteLine("[Test PutGet] DHT engines reached Ready state.");

            // --- BEP46 Mutable Put/Get ---
            using var keyA = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
            var publicKeyA = new BEncodedString(keyA.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
            var salt = new BEncodedString("test-salt");
            var valueToPut = new BEncodedString("Hello from Peer A!");
            long sequenceNumber = 1;

            var dataToSign = new List<byte>();
            dataToSign.AddRange(Encoding.UTF8.GetBytes($"seq{sequenceNumber}"));
            if (salt != null) dataToSign.AddRange(Encoding.UTF8.GetBytes($"4:salt{Encoding.UTF8.GetByteCount(salt.Text)}:{salt.Text}"));
            dataToSign.AddRange(Encoding.UTF8.GetBytes($"1:v{Encoding.UTF8.GetByteCount(valueToPut.Text)}:{valueToPut.Text}"));
            var signatureA = new BEncodedString(NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(keyA, dataToSign.ToArray()));

            Console.WriteLine($"[Test PutGet] Peer A ({dhtEngineA.LocalId}) putting mutable item via DHT...");
            await dhtEngineA.PutMutableAsync(publicKeyA, salt, valueToPut, sequenceNumber, signatureA);
            Console.WriteLine($"[Test PutGet] Peer A Put complete.");

            await Task.Delay(3000);

            Console.WriteLine($"[Test PutGet] Peer B ({dhtEngineB.LocalId}) getting mutable item via DHT...");
            var targetId = DhtEngine.CalculateMutableTargetId(publicKeyA, salt);
            (BEncodedValue? retrievedValue, BEncodedString? retrievedPk, BEncodedString? retrievedSig, long? retrievedSeq) = await dhtEngineB.GetAsync(targetId, sequenceNumber);
            Console.WriteLine($"[Test PutGet] Peer B Get complete.");

            Assert.IsNotNull(retrievedValue, "Retrieved value should not be null");
            Assert.AreEqual(valueToPut, retrievedValue, "Retrieved value mismatch");
            Assert.IsNotNull(retrievedPk, "Retrieved public key should not be null");
            Assert.IsTrue(publicKeyA.Span.SequenceEqual(retrievedPk!.Span), "Retrieved public key mismatch");
            Assert.IsNotNull(retrievedSig, "Retrieved signature should not be null");
            Assert.IsTrue(signatureA.Span.SequenceEqual(retrievedSig!.Span), "Retrieved signature mismatch");
            Assert.AreEqual(sequenceNumber, retrievedSeq, "Retrieved sequence number mismatch");
            Console.WriteLine($"[Test PutGet] Peer B successfully retrieved mutable item from Peer A via DHT.");
        }

        // TODO: Add Test_ExternalEndpoint_Advertisement
        // TODO: Add Test_NatsService_Initialization_Failure
        // TODO: Add Test_HolePunching_Attempt (might require mocking UdpClient)

        // Helper to wait for DHT Ready state
        private async Task WaitForReadyStateAsync(DhtEngine engine, TimeSpan timeout)
        {
            if (engine.State == DhtState.Ready)
                return;

            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

            EventHandler? handler = null;
            handler = (o, e) => {
                if (engine.State == DhtState.Ready)
                {
                    engine.StateChanged -= handler;
                    tcs.TrySetResult(true);
                }
            };

            engine.StateChanged += handler;

            if (engine.State == DhtState.Ready)
            {
                engine.StateChanged -= handler;
                tcs.TrySetResult(true);
            }

            try {
                await tcs.Task;
            } finally {
                engine.StateChanged -= handler;
            }
        }
    }
}
