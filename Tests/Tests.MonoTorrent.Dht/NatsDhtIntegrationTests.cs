using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;
using NATS.Client.Core; // Assuming NATS.Client.Core is used
using NATS.Client.JetStream; // May not be needed for basic pub/sub/req/rep
using NUnit.Framework;
using MonoTorrent.Client; // Add missing using

namespace MonoTorrent.Dht
{
    [TestFixture]
    public class NatsDhtIntegrationTests
    {
        // TODO: Configure NATS server URL (e.g., from environment variable or local default)
        private static readonly string NatsServerUrl = "nats://demo.nats.io:4222"; // Use demo server
        private static readonly NatsOpts NatsOptions = NatsOpts.Default with { Url = NatsServerUrl };

        private ClientEngine? engineA;
        private ClientEngine? engineB;
        private NatsNatTraversalService? natsServiceA;
        private NatsNatTraversalService? natsServiceB;

        // Placeholder for a simple NATS echo service for discovery
        private Task? _natsDiscoveryServiceTask;
        private CancellationTokenSource? _natsDiscoveryCts;

        [SetUp]
        public void Setup()
        {
            // Reset engines and services before each test
            // Dispose ClientEngines first, which should handle DHT engine disposal.
            engineA?.Dispose();
            engineB?.Dispose();
            // Dispose NATS services separately.
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();
            _natsDiscoveryCts?.Cancel();
            _natsDiscoveryCts?.Dispose();

            // Use default EngineSettings, which should enable DHT by default if an endpoint is provided.
            // We'll provide the DHT endpoint later via the listener.
            var settings = new EngineSettingsBuilder {
                 // Provide a DHT endpoint to ensure a real DhtEngine is created.
                 // Port 0 allows the OS to assign an available port.
                 DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                 AutoSaveLoadDhtCache = true // Keep this setting
            }.ToSettings();

            engineA = new ClientEngine(settings);
            engineB = new ClientEngine(settings);

            // Start a placeholder discovery service for tests
            _natsDiscoveryCts = new CancellationTokenSource();
            _natsDiscoveryServiceTask = StartPlaceholderDiscoveryServiceAsync(NatsOptions, _natsDiscoveryCts.Token);
            // Give the placeholder service a moment to start and subscribe
            Thread.Sleep(200); // Use Thread.Sleep in Setup as it's synchronous
        }

        [TearDown]
        public async Task Teardown()
        {
            _natsDiscoveryCts?.Cancel();
            if (_natsDiscoveryServiceTask != null)
            {
                try { await _natsDiscoveryServiceTask; } catch { /* Ignore cancellation */ }
            }
            _natsDiscoveryCts?.Dispose();
            engineA?.Dispose();
            engineB?.Dispose();
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();
            // Give NATS connections time to close gracefully if needed
            await Task.Delay(100);
        }

        // Placeholder NATS Discovery Service (Simulates responding to discovery requests)
        private async Task StartPlaceholderDiscoveryServiceAsync(NatsOpts opts, CancellationToken token)
        {
            await Task.Yield(); // Ensure it runs async
            try
            {
                var nats = new NatsConnection(opts);
                await nats.ConnectAsync();
                Console.WriteLine("[Discovery Service] Connected to NATS.");
                await foreach (var msg in nats.SubscribeAsync<byte[]>("p2p.discovery.request", cancellationToken: token))
                {
                    try
                    {
                        // Simulate discovering the external IP/Port.
                        string fakeExternalIp = "1.2.3.4"; // Example fake IP
                        int fakeExternalPort = new Random().Next(10000, 60000); // Example fake port
                        string replyData = $"{fakeExternalIp}|{fakeExternalPort}";
                        Console.WriteLine($"[Discovery Service] Replying to {msg.ReplyTo} with {replyData}");
                        await msg.ReplyAsync(Encoding.UTF8.GetBytes(replyData), cancellationToken: token); // Encode reply as bytes
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                         Console.WriteLine($"[Discovery Service Error] {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { Console.WriteLine("[Discovery Service] Stopped."); }
            catch (Exception ex) { Console.WriteLine($"[Discovery Service Connection Error] {ex.Message}"); }
        }


        [Test]
        public async Task Test_NatsDiscovery_And_Bootstrap()
        {
            Assert.IsNotNull(engineA!, "ClientEngine A should not be null"); // Add null-forgiving operator
            Assert.IsNotNull(engineB!, "ClientEngine B should not be null"); // Add null-forgiving operator

            // NATS services require the DhtEngine's LocalId, which is accessible via ClientEngine.DhtEngine
            Assert.IsNotNull(engineA!.DhtEngine as DhtEngine, "EngineA's DhtEngine should be a DhtEngine instance"); // Cast and check
            Assert.IsNotNull(engineB!.DhtEngine as DhtEngine, "EngineB's DhtEngine should be a DhtEngine instance"); // Cast and check

            natsServiceA = new NatsNatTraversalService(NatsOptions, ((DhtEngine)engineA!.DhtEngine).LocalId); // Cast to access LocalId
            natsServiceB = new NatsNatTraversalService(NatsOptions, ((DhtEngine)engineB!.DhtEngine).LocalId); // Cast to access LocalId

            // Initialize NATS services explicitly before starting the engine/torrents
            try
            {
                await engineA!.DhtEngine.InitializeNatAsync(natsServiceA!); // Add null-forgiving operators
                await engineB!.DhtEngine.InitializeNatAsync(natsServiceB!); // Add null-forgiving operators
            }
            catch (Exception ex)
            {
                 Assert.Fail($"NATS Service InitializeNatAsync failed: {ex.Message}");
                 return;
            }

            // Add dummy torrents to trigger DHT engine startup
            var magnetLinkA = new MagnetLink(InfoHashes.FromV1(new InfoHash(new byte[20])), "TorrentA");
            var magnetLinkB = new MagnetLink(InfoHashes.FromV1(new InfoHash(Enumerable.Repeat((byte)0xFF, 20).ToArray())), "TorrentB");

            var managerA = await engineA!.AddAsync(magnetLinkA, "dummy_save_path_A"); // Add null-forgiving operator
            var managerB = await engineB!.AddAsync(magnetLinkB, "dummy_save_path_B"); // Add null-forgiving operator

            // Start the torrents (this implicitly starts the ClientEngine's DHT)
            try
            {
                await managerA.StartAsync();
                await managerB.StartAsync();
            } catch (Exception ex) {
                Assert.Fail ($"TorrentManager StartAsync failed: {ex.Message}");
                return; // Prevent further execution if start fails
            }

            // Assert that the DHT engines discovered their external endpoints after startup
            Assert.IsNotNull(((DhtEngine)engineA!.DhtEngine).ExternalEndPoint, "EngineA ExternalEndPoint should be discovered after starting engine"); // Cast to access ExternalEndPoint
            Assert.IsNotNull(((DhtEngine)engineB!.DhtEngine).ExternalEndPoint, "EngineB ExternalEndPoint should be discovered after starting engine"); // Cast to access ExternalEndPoint


            

            // Verify external endpoints were discovered (using placeholder values from echo service - assuming 1.2.3.4)
            // Note: The actual IP/Port will depend on the NATS discovery service response.
            Assert.AreEqual(IPAddress.Parse("1.2.3.4"), ((DhtEngine)engineA!.DhtEngine).ExternalEndPoint?.Address, "EngineA IP mismatch"); // Cast to access ExternalEndPoint
            Assert.AreEqual(IPAddress.Parse("1.2.3.4"), ((DhtEngine)engineB!.DhtEngine).ExternalEndPoint?.Address, "EngineB IP mismatch"); // Cast to access ExternalEndPoint
            Assert.IsTrue(((DhtEngine)engineA!.DhtEngine).ExternalEndPoint?.Port > 0, "EngineA Port invalid"); // Cast to access ExternalEndPoint
            Assert.IsTrue(((DhtEngine)engineB!.DhtEngine).ExternalEndPoint?.Port > 0, "EngineB Port invalid"); // Cast to access ExternalEndPoint

            // No need to manually add nodes now, they were provided during StartAsync

            // Wait for engines to become ready
            Console.WriteLine("[Test Bootstrap] Waiting for DHT engines to reach Ready state...");
            var timeout = TimeSpan.FromSeconds(60); // Use the increased timeout
            var readyTaskA = WaitForReadyStateAsync(engineA!, timeout); // Add null-forgiving operator
            var readyTaskB = WaitForReadyStateAsync(engineB!, timeout); // Add null-forgiving operator
            await Task.WhenAll(readyTaskA, readyTaskB);

            // Verify both engines reach Ready state
            Assert.AreEqual(DhtState.Ready, engineA!.Dht.State, $"EngineA DHT did not reach Ready state within {timeout.TotalSeconds}s"); // Add null-forgiving operator
            Assert.AreEqual(DhtState.Ready, engineB!.Dht.State, $"EngineB DHT did not reach Ready state within {timeout.TotalSeconds}s"); // Add null-forgiving operator

            // Test passed if endpoints are discovered and DHT reaches ready state.
        }

        [Test]
        public async Task Test_NatsDiscovery_Then_DHT_PutGet()
        {
            Assert.IsNotNull(engineA!, "ClientEngine A should not be null"); // Add null-forgiving operator
            Assert.IsNotNull(engineB!, "ClientEngine B should not be null"); // Add null-forgiving operator
            // Placeholder discovery service task removed

            // NATS services require the DhtEngine's LocalId
            Assert.IsNotNull(engineA!.DhtEngine as DhtEngine, "EngineA's DhtEngine should be a DhtEngine instance"); // Cast and check
            Assert.IsNotNull(engineB!.DhtEngine as DhtEngine, "EngineB's DhtEngine should be a DhtEngine instance"); // Cast and check

            natsServiceA = new NatsNatTraversalService(NatsOptions, ((DhtEngine)engineA!.DhtEngine).LocalId); // Cast to access LocalId
            natsServiceB = new NatsNatTraversalService(NatsOptions, ((DhtEngine)engineB!.DhtEngine).LocalId); // Cast to access LocalId

            // Initialize NATS services explicitly before starting the engine/torrents
            try
            {
                await engineA!.DhtEngine.InitializeNatAsync(natsServiceA!); // Add null-forgiving operators
                await engineB!.DhtEngine.InitializeNatAsync(natsServiceB!); // Add null-forgiving operators
            }
            catch (Exception ex)
            {
                 Assert.Fail($"NATS Service InitializeNatAsync failed for PutGet test: {ex.Message}");
                 return;
            }
 
            // No initial nodes before NAT traversal completes
            var emptyNodes = ReadOnlyMemory<byte>.Empty;
 
            // Add dummy torrents to trigger DHT engine startup
            var magnetLinkA = new MagnetLink(InfoHashes.FromV1(new InfoHash(new byte[20])), "TorrentA");
            var magnetLinkB = new MagnetLink(InfoHashes.FromV1(new InfoHash(Enumerable.Repeat((byte)0xFF, 20).ToArray())), "TorrentB");

            var managerA = await engineA!.AddAsync(magnetLinkA, "dummy_save_path_A"); // Add null-forgiving operator
            var managerB = await engineB!.AddAsync(magnetLinkB, "dummy_save_path_B"); // Add null-forgiving operator

            // Start the torrents (this implicitly starts the ClientEngine's DHT)
            try
            {
                await managerA.StartAsync();
                await managerB.StartAsync();
            } catch (Exception ex) {
                Assert.Fail ($"TorrentManager StartAsync failed for PutGet test: {ex.Message}");
                return; // Prevent further execution if start fails
            }

            // Assert that the DHT engines discovered their external endpoints after startup
            Assert.IsNotNull(((DhtEngine)engineA!.DhtEngine).ExternalEndPoint, "EngineA ExternalEndPoint should be discovered after starting engine"); // Cast to access ExternalEndPoint
            Assert.IsNotNull(((DhtEngine)engineB!.DhtEngine).ExternalEndPoint, "EngineB ExternalEndPoint should be discovered after starting engine"); // Cast to access ExternalEndPoint
 
            // No need to manually add nodes now
            // if (engineA.ExternalEndPoint != null && engineB.ExternalEndPoint != null)
            // {
            //     var nodeBInfo = new Node(engineB.LocalId, engineB.ExternalEndPoint).CompactNode().AsMemory();
            //     engineA.Add(new[] { nodeBInfo });
            //     var nodeAInfo = new Node(engineA.LocalId, engineA.ExternalEndPoint).CompactNode().AsMemory();
            //     engineB.Add(new[] { nodeAInfo });
            // }
            //  else
            // {
            //     Assert.Fail("External endpoints were not discovered for PutGet test.");
            // }
 
            // Wait for engines to become ready
            Console.WriteLine("[Test PutGet] Waiting for DHT engines to reach Ready state...");
            var timeout = TimeSpan.FromSeconds(60); // Use increased timeout
            var readyTaskA = WaitForReadyStateAsync(engineA!, timeout); // Add null-forgiving operator
            var readyTaskB = WaitForReadyStateAsync(engineB!, timeout); // Add null-forgiving operator
            await Task.WhenAll(readyTaskA, readyTaskB);

            // Ensure engines are ready
            Assert.AreEqual(DhtState.Ready, engineA!.Dht.State, $"EngineA DHT not Ready for PutGet within {timeout.TotalSeconds}s"); // Add null-forgiving operator
            Assert.AreEqual(DhtState.Ready, engineB!.Dht.State, $"EngineB DHT not Ready for PutGet within {timeout.TotalSeconds}s"); // Add null-forgiving operator

            // --- BEP46 Mutable Put/Get ---
            // Generate keypair for Peer A (using a library like NSec.Cryptography)
            using var keyA = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
            var publicKeyA = new BEncodedString(keyA.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
            var salt = new BEncodedString("test-salt"); // Optional salt
            var valueToPut = new BEncodedString("Hello from Peer A!");
            long sequenceNumber = 1;

            // Sign the data: seq + salt (optional) + value
            var dataToSign = new List<byte>();
            dataToSign.AddRange(Encoding.UTF8.GetBytes($"seq{sequenceNumber}"));
            // Use Encoding.UTF8.GetByteCount(Text) for the byte length of the string content
            if (salt != null) dataToSign.AddRange(Encoding.UTF8.GetBytes($"4:salt{Encoding.UTF8.GetByteCount(salt.Text)}:{salt.Text}"));
            // Use Encoding.UTF8.GetByteCount(Text) for the byte length of the string content
            dataToSign.AddRange(Encoding.UTF8.GetBytes($"1:v{Encoding.UTF8.GetByteCount(valueToPut.Text)}:{valueToPut.Text}"));

            var signatureA = new BEncodedString(NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(keyA, dataToSign.ToArray()));

            Console.WriteLine($"[Test PutGet] Peer A ({engineA!.PeerId}) putting mutable item via DHT..."); // Add null-forgiving operator
            await engineA!.Dht.PutMutableAsync(publicKeyA, salt, valueToPut, sequenceNumber, signatureA); // Add null-forgiving operator
            Console.WriteLine($"[Test PutGet] Peer A Put complete.");

            // Allow time for Put to propagate (DHT is eventually consistent)
            await Task.Delay(3000); // Increase delay for Put propagation

            Console.WriteLine($"[Test PutGet] Peer B ({engineB!.PeerId}) getting mutable item via DHT..."); // Add null-forgiving operator
            var targetId = DhtEngine.CalculateMutableTargetId(publicKeyA, salt);
            (BEncodedValue? retrievedValue, BEncodedString? retrievedPk, BEncodedString? retrievedSig, long? retrievedSeq) = await engineB!.Dht.GetAsync(targetId, sequenceNumber); // Add null-forgiving operator and explicit types
            Console.WriteLine($"[Test PutGet] Peer B Get complete.");

            Assert.IsNotNull(retrievedValue, "Retrieved value should not be null");
            Assert.AreEqual(valueToPut, retrievedValue, "Retrieved value mismatch");
            Assert.IsNotNull(retrievedPk, "Retrieved public key should not be null");
            Assert.IsTrue(publicKeyA.Span.SequenceEqual(retrievedPk!.Span), "Retrieved public key mismatch");
            Assert.IsNotNull(retrievedSig, "Retrieved signature should not be null");
            Assert.IsTrue(signatureA.Span.SequenceEqual(retrievedSig!.Span), "Retrieved signature mismatch");
            Assert.AreEqual(sequenceNumber, retrievedSeq, "Retrieved sequence number mismatch");
            Console.WriteLine($"[Test PutGet] Peer B successfully retrieved mutable item from Peer A via DHT.");

            // --- Optional: BEP44 Immutable Put/Get ---
            // ... (Implementation would be similar: Peer B puts, Peer A gets using SHA1 hash of value) ...
        }

        // TODO: Add Test_ExternalEndpoint_Advertisement
        // TODO: Add Test_NatsService_Initialization_Failure
        // TODO: Add Test_HolePunching_Attempt (might require mocking UdpClient)

        // Helper to wait for DHT Ready state
        // Modified helper to wait for ClientEngine's DHT Ready state
        private async Task WaitForReadyStateAsync(ClientEngine engine, TimeSpan timeout)
        {
            if (engine.Dht.State == DhtState.Ready)
                return;

            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

            EventHandler? handler = null;
            handler = (o, e) => {
                if (engine.Dht.State == DhtState.Ready)
                {
                    engine.DhtEngine.StateChanged -= handler;
                    tcs.TrySetResult(true);
                }
            };

            engine.Dht.StateChanged += handler; // Subscribe to the wrapped DHT engine's event

            // Re-check state after attaching handler in case it changed right before
            if (engine.Dht.State == DhtState.Ready)
            {
                engine.Dht.StateChanged -= handler;
                tcs.TrySetResult(true);
            }

            try {
                await tcs.Task;
            } finally {
                // Ensure handler is removed even on timeout/cancellation
                engine.Dht.StateChanged -= handler;
            }
        }
    }
}
