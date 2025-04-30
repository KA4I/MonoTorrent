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
using Org.BouncyCastle.Crypto.Parameters; // Added for Ed25519 keys
using Org.BouncyCastle.Crypto.Signers;   // Added for Ed25519 signing
using System.Security.Cryptography; // Added for SHA1
using Org.BouncyCastle.Security;         // Added for SecureRandom, GeneratorUtilities

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
 private BEncodedString peerIdA; // Use BEncodedString for PeerId representation
 private BEncodedString peerIdB;
      
        // Removed placeholder discovery service fields
      
      
        [SetUp]
        public void Setup ()
        {
            dhtEngineA?.Dispose ();
            dhtEngineB?.Dispose ();
            listenerA?.Stop ();
            listenerB?.Stop ();
            natsServiceA?.Dispose ();
            natsServiceB?.Dispose ();
            // Removed placeholder discovery service cleanup

            dhtEngineA = new DhtEngine ();
            dhtEngineB = new DhtEngine ();
   // Generate random 20-byte PeerIDs and store as BEncodedString
   var idBytesA = new byte[20];
   var idBytesB = new byte[20];
			using (var rng = RandomNumberGenerator.Create()) {
				rng.GetBytes (idBytesA);
				rng.GetBytes (idBytesB);
			}
   peerIdA = new BEncodedString (idBytesA);
   peerIdB = new BEncodedString (idBytesB);
            // Removed placeholder discovery list initialization
         
            listenerA = new DhtListener (new IPEndPoint (IPAddress.Loopback, 0));
            listenerB = new DhtListener (new IPEndPoint (IPAddress.Loopback, 0));
            dhtEngineA.SetListenerAsync (listenerA).GetAwaiter ().GetResult ();
            dhtEngineB.SetListenerAsync (listenerB).GetAwaiter ().GetResult ();

            listenerA.Start ();
            listenerB.Start ();

            var sw = System.Diagnostics.Stopwatch.StartNew ();
            while ((listenerA.Status != ListenerStatus.Listening || listenerB.Status != ListenerStatus.Listening) && sw.Elapsed < TimeSpan.FromSeconds (5)) {
                Thread.Sleep (50);
            }

            Assert.AreEqual (ListenerStatus.Listening, listenerA.Status, "Listener A did not start listening.");
            Assert.AreEqual (ListenerStatus.Listening, listenerB.Status, "Listener B did not start listening.");
            Assert.IsNotNull (listenerA.LocalEndPoint, "Listener A failed to bind.");
            Assert.IsNotNull (listenerB.LocalEndPoint, "Listener B failed to bind.");

            // Removed placeholder discovery service startup
        }

        [TearDown]
        public async Task Teardown ()
        {
            // Removed placeholder discovery service teardown
            listenerA?.Stop ();
            listenerB?.Stop ();
            dhtEngineA?.Dispose ();
            dhtEngineB?.Dispose ();
            natsServiceA?.Dispose ();
            natsServiceB?.Dispose ();
            await Task.Delay (100);
        }


        [Test]
        public async Task Test_NatsDiscovery_Then_DHT_PutGet ()
        {
            Assert.IsNotNull (dhtEngineA, "DhtEngine A should not be null");
            Assert.IsNotNull (dhtEngineB, "DhtEngine B should not be null");
            Assert.IsNotNull (listenerA?.LocalEndPoint, "Listener A should have a local endpoint");
            Assert.IsNotNull (listenerB?.LocalEndPoint, "Listener B should have a local endpoint");
            // Removed assertion for _localEndpointsForDiscovery

        			// Instantiate NATS services, passing the correct PeerId
   natsServiceA = new NatsNatTraversalService (NatsOptions, dhtEngineA!.LocalId, peerIdA); // Pass BEncodedString directly
   natsServiceB = new NatsNatTraversalService (NatsOptions, dhtEngineB!.LocalId, peerIdB); // Pass BEncodedString directly
        			// Initialize NATS services
            var initTaskA_pg = natsServiceA.InitializeAsync (listenerA!, portForwarder: null);
            var initTaskB_pg = natsServiceB.InitializeAsync (listenerB!, portForwarder: null);
            try {
                await Task.WhenAll (initTaskA_pg, initTaskB_pg);
                Console.WriteLine ("[Test PutGet] NATS Services Initialized.");
            } catch (Exception ex) {
                Assert.Fail ($"NATS Service InitializeAsync failed unexpectedly for PutGet: {ex.Message}");
                return;
            }


            // Wait for peers to discover each other via NATS, with a timeout
            Console.WriteLine ("[Test PutGet] Waiting for NATS peer discovery...");
            var discoveryTimeout_pg = TimeSpan.FromSeconds (20); // Increased timeout
            var discoverySw_pg = System.Diagnostics.Stopwatch.StartNew ();
            bool discoveredA_pg_status = false;
            bool discoveredB_pg_status = false;
        			IDictionary<NodeId, IPEndPoint>? discoveredByA_pg = null;
        			IDictionary<NodeId, IPEndPoint>? discoveredByB_pg = null;
        
   // We need to check if the dictionary contains the *actual* NodeId of the other peer,
   // as this is the key used by NatsNatTraversalService based on the received NATS message.
   // The hashing logic was based on TorrentServiceTests where NodeId is derived from PeerId,
   // which is not the case in this specific test setup.
   var expectedNodeIdA = dhtEngineA!.LocalId;
   var expectedNodeIdB = dhtEngineB!.LocalId;
        
        			while (discoverySw_pg.Elapsed < discoveryTimeout_pg && (!discoveredA_pg_status || !discoveredB_pg_status)) {
        				discoveredByA_pg = natsServiceA.GetDiscoveredPeers ();
        				discoveredByB_pg = natsServiceB.GetDiscoveredPeers ();
    discoveredA_pg_status = discoveredByA_pg?.ContainsKey (expectedNodeIdB) ?? false; // Check for Peer B's actual NodeId
    discoveredB_pg_status = discoveredByB_pg?.ContainsKey (expectedNodeIdA) ?? false; // Check for Peer A's actual NodeId
        				if (!discoveredA_pg_status || !discoveredB_pg_status)
        					await Task.Delay (250); // Poll every 250ms
        			}
            discoverySw_pg.Stop ();
            Console.WriteLine ($"[Test PutGet] Peer discovery loop finished after {discoverySw_pg.ElapsedMilliseconds}ms. DiscoveredA: {discoveredA_pg_status}, DiscoveredB: {discoveredB_pg_status}");

            // Discovered peers retrieved within the loop above

        			Console.WriteLine ($"[Test PutGet] Peers discovered by A: {discoveredByA_pg?.Count ?? 0}");
        			Console.WriteLine ($"[Test PutGet] Peers discovered by B: {discoveredByB_pg?.Count ?? 0}");
        
        			// Assert discovery after the wait loop
        			Assert.IsTrue (discoveredA_pg_status, "NATS Service A did not discover Peer B for PutGet within timeout.");
        			Assert.IsTrue (discoveredB_pg_status, "NATS Service B did not discover Peer A for PutGet within timeout.");
        
   var endpointB_discoveredByA_pg = discoveredByA_pg![expectedNodeIdB]; // Use actual NodeId B
   var endpointA_discoveredByB_pg = discoveredByB_pg![expectedNodeIdA]; // Use actual NodeId A
        
        			dhtEngineA.ExternalEndPoint = natsServiceA.MyExternalEndPoint; // Set for consistency if needed elsewhere
        			dhtEngineB.ExternalEndPoint = natsServiceB.MyExternalEndPoint;

            // Create nodes using the discovered endpoints
            var nodeA_forB_pg = new Node (dhtEngineA.LocalId, endpointA_discoveredByB_pg);
            var nodeB_forA_pg = new Node (dhtEngineB.LocalId, endpointB_discoveredByA_pg);

            // var initialNodesA = Node.CompactNode (new[] { nodeB_forA_pg }).AsMemory (); // Removed initial nodes
            // var initialNodesB = Node.CompactNode (new[] { nodeA_forB_pg }).AsMemory (); // Removed initial nodes

            Console.WriteLine ($"[Test PutGet] Starting DHT Engine A");
            Console.WriteLine ($"[Test PutGet] Starting DHT Engine B");
            // Start engines without initial nodes; they should discover each other on loopback.
            var startTaskA = dhtEngineA.StartAsync (Array.Empty<byte> (), Array.Empty<string> ());
            var startTaskB = dhtEngineB.StartAsync (Array.Empty<byte> (), Array.Empty<string> ());
            await Task.WhenAll (startTaskA, startTaskB);
            Console.WriteLine ("[Test PutGet] DHT engines started.");

            // Allow time for routing tables to populate via pings/responses
            Console.WriteLine ("[Test PutGet] Waiting 10 seconds for routing tables to populate...");
            await Task.Delay (TimeSpan.FromSeconds (10));

            // Verify routing tables contain each other
            Assert.IsNotNull (dhtEngineA.RoutingTable.FindNode (expectedNodeIdB), "Engine A routing table should contain Engine B.");
            Assert.IsNotNull (dhtEngineB.RoutingTable.FindNode (expectedNodeIdA), "Engine B routing table should contain Engine A.");
            Console.WriteLine ("[Test PutGet] Routing tables populated.");


            Console.WriteLine ("[Test PutGet] Waiting for DHT engines to reach Ready state...");
            var timeout = TimeSpan.FromSeconds (15);
            var readyTaskA = WaitForReadyStateAsync (dhtEngineA, timeout);
            var readyTaskB = WaitForReadyStateAsync (dhtEngineB, timeout);
            await Task.WhenAll (readyTaskA, readyTaskB);

            Assert.AreEqual (DhtState.Ready, dhtEngineA.State, $"EngineA DHT not Ready for PutGet within {timeout.TotalSeconds}s");
            Assert.AreEqual (DhtState.Ready, dhtEngineB.State, $"EngineB DHT not Ready for PutGet within {timeout.TotalSeconds}s");
            Console.WriteLine ("[Test PutGet] DHT engines reached Ready state.");

            // --- BEP46 Mutable Put/Get ---
            // Generate Ed25519 keys using BouncyCastle
            var random = new SecureRandom ();
            var keyPairGenerator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator ();
            keyPairGenerator.Init (new Org.BouncyCastle.Crypto.KeyGenerationParameters (random, 256));
            var keyPair = keyPairGenerator.GenerateKeyPair ();
            var privateKeyParams = (Ed25519PrivateKeyParameters) keyPair.Private;
            var publicKeyParams = (Ed25519PublicKeyParameters) keyPair.Public;

            var publicKeyA = new BEncodedString (publicKeyParams.GetEncoded ()); // Get raw public key bytes
            var salt = new BEncodedString ("test-salt");
            var valueToPut = new BEncodedString ("Hello from Peer A!");
            long sequenceNumber = 1;

            // Sign the data using BouncyCastle and the format expected by VerifyMutableSignature
            var signatureA = SignMutableDataHelper (privateKeyParams, salt, sequenceNumber, valueToPut);
            Console.WriteLine ($"[Test PutGet] Peer A ({dhtEngineA.LocalId}) putting mutable item via DHT...");
            await dhtEngineA.PutMutableAsync (publicKeyA, salt, valueToPut, sequenceNumber, signatureA);
            Console.WriteLine ($"[Test PutGet] Peer A Put complete.");

            await Task.Delay (3000);

            Console.WriteLine ($"[Test PutGet] Peer B ({dhtEngineB.LocalId}) getting mutable item via DHT...");
            var targetId = DhtEngine.CalculateMutableTargetId (publicKeyA, salt);
            (BEncodedValue? retrievedValue, BEncodedString? retrievedPk, BEncodedString? retrievedSig, long? retrievedSeq) = await dhtEngineB.GetAsync (targetId, sequenceNumber);
            Console.WriteLine ($"[Test PutGet] Peer B Get complete.");

            Assert.IsNotNull (retrievedValue, "Retrieved value should not be null");
            Assert.AreEqual (valueToPut, retrievedValue, "Retrieved value mismatch");
            Assert.IsNotNull (retrievedPk, "Retrieved public key should not be null");
            Assert.IsTrue (publicKeyA.Span.SequenceEqual (retrievedPk!.Span), "Retrieved public key mismatch");
            Assert.IsNotNull (retrievedSig, "Retrieved signature should not be null");
            Assert.IsTrue (signatureA.Span.SequenceEqual (retrievedSig!.Span), "Retrieved signature mismatch");
            Assert.AreEqual (sequenceNumber, retrievedSeq, "Retrieved sequence number mismatch");
            Console.WriteLine ($"[Test PutGet] Peer B successfully retrieved mutable item from Peer A via DHT.");
        }

        // TODO: Add Test_ExternalEndpoint_Advertisement
        // TODO: Add Test_NatsService_Initialization_Failure
        // TODO: Add Test_HolePunching_Attempt (might require mocking UdpClient)

        // Helper to wait for DHT Ready state
        private async Task WaitForReadyStateAsync (DhtEngine engine, TimeSpan timeout)
        {
            if (engine.State == DhtState.Ready)
                return;

            var tcs = new TaskCompletionSource<bool> ();
            using var cts = new CancellationTokenSource (timeout);
            using var registration = cts.Token.Register (() => tcs.TrySetCanceled ());

            EventHandler? handler = null;
            handler = (o, e) => {
                if (engine.State == DhtState.Ready) {
                    engine.StateChanged -= handler;
                    tcs.TrySetResult (true);
                }
            };

            engine.StateChanged += handler;

            if (engine.State == DhtState.Ready) {
                engine.StateChanged -= handler;
                tcs.TrySetResult (true);
            }

            try {
                await tcs.Task;
            } finally {
                engine.StateChanged -= handler;
            }
        }


        // Helper function to sign mutable data (adapted from TorrentManagerTests)
        private static BEncodedString SignMutableDataHelper (Ed25519PrivateKeyParameters privateKey, BEncodedString? salt, long sequenceNumber, BEncodedValue value)
        {
            // Construct the data to sign: "salt" + salt + "seq" + seq + "v" + value
            int saltKeyLength = new BEncodedString ("salt").LengthInBytes ();
            int seqKeyLength = new BEncodedString ("seq").LengthInBytes ();
            int vKeyLength = new BEncodedString ("v").LengthInBytes ();

            int saltLength = (salt == null || salt.Span.Length == 0) ? 0 : (saltKeyLength + salt.LengthInBytes ());
            int seqLength = seqKeyLength + new BEncodedNumber (sequenceNumber).LengthInBytes ();
            int valueLength = vKeyLength + value.LengthInBytes ();
            int totalLength = saltLength + seqLength + valueLength;

            using var rented = System.Buffers.MemoryPool<byte>.Shared.Rent (totalLength);
            Span<byte> dataToSign = rented.Memory.Span.Slice (0, totalLength);

            int offset = 0;
            if (saltLength > 0) {
                offset += new BEncodedString ("salt").Encode (dataToSign.Slice (offset));
                offset += salt!.Encode (dataToSign.Slice (offset));
            }
            offset += new BEncodedString ("seq").Encode (dataToSign.Slice (offset));
            offset += new BEncodedNumber (sequenceNumber).Encode (dataToSign.Slice (offset));
            offset += new BEncodedString ("v").Encode (dataToSign.Slice (offset));
            offset += value.Encode (dataToSign.Slice (offset));

            // Sign the data
            var signer = new Ed25519Signer ();
            signer.Init (true, privateKey); // true for signing
            signer.BlockUpdate (dataToSign.ToArray (), 0, dataToSign.Length); // Use the byte array overload
            byte[] signatureBytes = signer.GenerateSignature ();

            return new BEncodedString (signatureBytes);
        }
    }
}  
