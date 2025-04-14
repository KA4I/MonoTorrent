using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using MonoTorrent.PortForwarding;
using NATS.Client.Core;
using NUnit.Framework;

namespace MonoTorrent.Client
{
    [TestFixture]
    [Category("Integration"), Category("RequiresNetwork")] // Mark as integration test requiring network/router
    public class ClientEngineRealNatNatsTests
    {
        // --- Test Setup ---
        private static readonly string NatsServerUrl = "nats://demo.nats.io:4222";
        private static readonly NatsOpts NatsOptions = NatsOpts.Default with { Url = NatsServerUrl };

        private ClientEngine? engineA;
        private ClientEngine? engineB;
        private NatsNatTraversalService? natsServiceA;
        private NatsNatTraversalService? natsServiceB;

        [SetUp]
        public void Setup()
        {
            // Ensure previous instances are disposed if tests run sequentially
            engineA?.Dispose();
            engineB?.Dispose();
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();

            // Use default factories to get the real MonoNatPortForwarder
            var settingsA = new EngineSettingsBuilder {
                AllowPortForwarding = true, // Enable port forwarding
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0), // Use ephemeral ports
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } }
            }.ToSettings(); // Use ToSettings() instead of Build()

            var settingsB = new EngineSettingsBuilder {
                AllowPortForwarding = true,
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } }
            }.ToSettings(); // Use ToSettings() instead of Build()

            engineA = new ClientEngine(settingsA); // Uses default factories
            engineB = new ClientEngine(settingsB); // Uses default factories

            // Nats services need the NodeId from the DhtEngine
            natsServiceA = new NatsNatTraversalService(NatsOptions, engineA.DhtEngine.LocalId, engineA.PeerId); // Pass PeerId
            natsServiceB = new NatsNatTraversalService(NatsOptions, engineB.DhtEngine.LocalId, engineB.PeerId); // Pass PeerId
        }

        [TearDown]
        public void Teardown()
        {
            engineA?.Dispose();
            engineB?.Dispose();
            natsServiceA?.Dispose();
            natsServiceB?.Dispose();
            // Give Mono.Nat time to potentially clean up mappings if StopAsync wasn't awaited fully
            Thread.Sleep(1000);
        }

        [Test]
        public async Task Test_ClientEngine_RealUPnP_Nats_Discovery()
        {
            Assert.IsNotNull(engineA, "Engine A should not be null");
            Assert.IsNotNull(engineB, "Engine B should not be null");
            Assert.IsNotNull(natsServiceA, "NATS Service A should not be null");
            Assert.IsNotNull(natsServiceB, "NATS Service B should not be null");

            // Start engines - this triggers real UPnP discovery and mapping attempts
            await engineA.StartAsync();
            await engineB.StartAsync();

            // VERY IMPORTANT: Allow significant time for real UPnP discovery and mapping.
            // This duration is highly dependent on the network and router.
            Console.WriteLine("[Test] Waiting 15 seconds for UPnP/NAT-PMP discovery and mapping...");
            await Task.Delay(TimeSpan.FromSeconds(15));
            Console.WriteLine("[Test] Finished waiting for UPnP/NAT-PMP.");

            // Initialize NATS services, passing the listener and the *real* port forwarder
            // Get listener via DhtEngine.MessageLoop
            var listenerA = ((DhtEngine)engineA.DhtEngine).MessageLoop.Listener; // Cast to access internal MessageLoop
            var listenerB = ((DhtEngine)engineB.DhtEngine).MessageLoop.Listener; // Cast to access internal MessageLoop
            var portForwarderA = engineA.PortForwarder; // Get the real one
            var portForwarderB = engineB.PortForwarder; // Get the real one

            Assert.IsNotNull(listenerA, "Engine A DHT Listener should not be null");
            Assert.IsNotNull(listenerB, "Engine B DHT Listener should not be null");
            Assert.IsNotNull(listenerA!.LocalEndPoint, "Listener A should be bound");
            Assert.IsNotNull(listenerB!.LocalEndPoint, "Listener B should be bound");

            var initTaskA = natsServiceA.InitializeAsync(listenerA!, portForwarderA);
            var initTaskB = natsServiceB.InitializeAsync(listenerB!, portForwarderB);
            await Task.WhenAll(initTaskA, initTaskB);
            Console.WriteLine("[Test] NATS Services Initialized.");

            // Wait for peers to discover each other via NATS
            Console.WriteLine("[Test] Waiting for NATS peer discovery...");
            var discoveryTimeout = TimeSpan.FromSeconds(20);
            var discoverySw = System.Diagnostics.Stopwatch.StartNew();
            bool discoveredA_status = false;
            bool discoveredB_status = false;
            IDictionary<NodeId, IPEndPoint> discoveredByA = null!;
            IDictionary<NodeId, IPEndPoint> discoveredByB = null!;

            while (discoverySw.Elapsed < discoveryTimeout && (!discoveredA_status || !discoveredB_status))
            {
                discoveredByA = natsServiceA.GetDiscoveredPeers();
                discoveredByB = natsServiceB.GetDiscoveredPeers();
                discoveredA_status = discoveredByA.ContainsKey(engineB.DhtEngine.LocalId); // Accessing LocalId via IDhtEngine
                discoveredB_status = discoveredByB.ContainsKey(engineA.DhtEngine.LocalId); // Accessing LocalId via IDhtEngine
                if (!discoveredA_status || !discoveredB_status)
                    await Task.Delay(250);
            }
            discoverySw.Stop();
            Console.WriteLine($"[Test] Peer discovery loop finished after {discoverySw.ElapsedMilliseconds}ms. DiscoveredA: {discoveredA_status}, DiscoveredB: {discoveredB_status}");

            // Assert discovery happened
            Assert.IsTrue(discoveredA_status, "NATS Service A did not discover Peer B within timeout.");
            Assert.IsTrue(discoveredB_status, "NATS Service B did not discover Peer A within timeout.");

            // Assert that the discovered endpoints are NOT the listener endpoints (implies UPnP/HTTP provided something)
            // This is a weaker assertion but more reliable than guessing the exact external IP/port.
            var discoveredEndpointB = discoveredByA[engineB.DhtEngine.LocalId];
            var discoveredEndpointA = discoveredByB[engineA.DhtEngine.LocalId];

            Console.WriteLine($"[Test] Endpoint for B discovered by A: {discoveredEndpointB}");
            Console.WriteLine($"[Test] Endpoint for A discovered by B: {discoveredEndpointA}");
            Console.WriteLine($"[Test] Listener B Local Endpoint: {listenerB.LocalEndPoint}");
            Console.WriteLine($"[Test] Listener A Local Endpoint: {listenerA.LocalEndPoint}");

            Assert.AreNotEqual(listenerB.LocalEndPoint, discoveredEndpointB, "Discovered endpoint for B should not be the listener fallback.");
            Assert.AreNotEqual(listenerA.LocalEndPoint, discoveredEndpointA, "Discovered endpoint for A should not be the listener fallback.");

            // We can also check if the IP is not loopback/any, suggesting public IP discovery worked
            Assert.IsFalse(IPAddress.IsLoopback(discoveredEndpointB.Address), "Discovered IP for B should not be loopback.");
            Assert.AreNotEqual(IPAddress.Any, discoveredEndpointB.Address, "Discovered IP for B should not be IPAddress.Any.");
            Assert.IsFalse(IPAddress.IsLoopback(discoveredEndpointA.Address), "Discovered IP for A should not be loopback.");
            Assert.AreNotEqual(IPAddress.Any, discoveredEndpointA.Address, "Discovered IP for A should not be IPAddress.Any.");

            Console.WriteLine("[Test] NATS discovery with real UPnP/HTTP endpoints verified successfully (endpoints are not fallbacks).");

            // Note: Further DHT operations (Put/Get) between these engines using the discovered
            // external endpoints might still fail unless the NAT devices allow hairpinning or
            // the test environment is specifically configured for it. This test primarily
            // verifies that the external endpoint discovery and NATS publishing work.
        }
    }
}