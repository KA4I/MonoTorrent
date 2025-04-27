using System;
using System.Linq; // Added for Linq
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using MonoTorrent.BEncoding;
using MonoTorrent.Connections; // Added for ListenerStatus
using MonoTorrent.Connections.Dht; // Added for IDhtListener
using MonoTorrent.Dht;
using MonoTorrent.Dht.Messages;
using Xunit;

namespace Tests.MonoTorrent.Dht
{
    public class RelayHopTests
    {
        // Reusable setup helper
        private (DhtEngine engine, Node node) CreateEngine(int port)
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var engine = new DhtEngine();
            // Assign listener (even a dummy one might be needed for some logic)
            // Ensure the listener is set *before* adding nodes, as adding implicitly pings
            engine.SetListenerAsync(new MockListener(endpoint)).Wait();
            var node = new Node(engine.LocalId, endpoint);
            return (engine, node);
        }

        [Fact]
        public async Task MultiHopRelay_AddsNodeToRoutingTable()
        {
            // Arrange
            var (engineA, nodeA) = CreateEngine(15001);
            var (engineB, nodeB) = CreateEngine(15002);
            var (engineC_Relay, nodeC_Relay) = CreateEngine(16000);

            // Engine B knows the Relay C
            engineB.RoutingTable.Add(nodeC_Relay);
            // Engine C (Relay) knows A and B
            engineC_Relay.RoutingTable.Add(nodeA);
            engineC_Relay.RoutingTable.Add(nodeB);

            // Mark nodes initially seen so they aren't immediately questionable/bad
            nodeA.Seen();
            nodeB.Seen();
            nodeC_Relay.Seen();

            // Create a Ping message from A to B
            var pingFromA = new Ping(nodeA.Id);
            pingFromA.TransactionId = new BEncodedString("relaytest_v2");
            var payload = pingFromA.Encode().ToArray();

            // Act
            // Simulate C relaying the message payload from A to B
            // Inject the message into B's engine as if received via relay from A
            engineB.InjectMessageFromRelay(payload, nodeA.Id);

            // Wait for the engine's main loop to process the injected message
            // Using Task.Delay is fragile. Await the main loop directly after delay.
            await Task.Delay(150); // Give time for queuing if necessary
            await DhtEngine.MainLoop; // Process anything queued on the loop

            // Assert
            // Check if Node A (the originator) is now present in Engine B's routing table
            var nodeAInBTable = engineB.RoutingTable.FindNode(nodeA.Id);

            // Debugging output
            Debug.WriteLine($"--- Routing Table B (Node {nodeB.Id.ToHex().Substring(0,6)}) ---");
            foreach (var bucket in engineB.RoutingTable.Buckets)
            {
                Debug.WriteLine($" Bucket Min: {bucket.Min.ToHex().Substring(0,6)} Max: {bucket.Max.ToHex().Substring(0,6)} Count: {bucket.Nodes.Count}");
                foreach (var n in bucket.Nodes)
                {
                    Debug.WriteLine($"  Node: {n.Id.ToHex().Substring(0,6)} EP: {n.EndPoint} State: {n.State} LastSeen: {n.LastSeen.TotalSeconds:F2}s FailedCount: {n.FailedCount}");
                }
                 if (bucket.Replacement != null)
                    Debug.WriteLine($"  Replacement: {bucket.Replacement.Id.ToHex().Substring(0,6)} EP: {bucket.Replacement.EndPoint} State: {bucket.Replacement.State}");

            }
            Debug.WriteLine($"FindNode(NodeA.Id = {nodeA.Id.ToHex().Substring(0,6)}) Result: {(nodeAInBTable == null ? "NULL" : "Found")}");

            // Print the endpoint that was found for nodeA in B's routing table
            if (nodeAInBTable != null)
            {
                Debug.WriteLine($"NodeA endpoint in B's table: {nodeAInBTable.EndPoint}");
                if (nodeAInBTable.EndPoint.Address.Equals(IPAddress.None))
                    Debug.WriteLine($"NodeA was added with IPAddress.None (dummy endpoint)");
            }
            else
            {
                Debug.WriteLine("NodeA was not found in B's routing table.");
            }

            Assert.NotNull(nodeAInBTable); // Check if the node was actually added/found

            // Optional: Check LastSeen as before, but only if the node is found
            if (nodeAInBTable != null)
            {
                 Assert.True(nodeAInBTable.LastSeen < TimeSpan.FromSeconds(1), // Should be seen very recently
                    $"LastSeen ({nodeAInBTable.LastSeen.TotalSeconds:F2}s) was not updated recently after processing relay message.");
            }
        }

        // Dummy listener for setup
        private class MockListener : IDhtListener
        {
             public IPEndPoint LocalEndPoint { get; }
             public ListenerStatus Status => ListenerStatus.Listening; // Assume always listening for test
             public event Action<ReadOnlyMemory<byte>, IPEndPoint> MessageReceived = delegate { };
             // Add missing event from IListener interface
             public event EventHandler<EventArgs>? StatusChanged;

             public MockListener(IPEndPoint endpoint) { LocalEndPoint = endpoint; }
             public void Dispose() { }
             public Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint) => Task.CompletedTask; // Don't actually send
             public void Start() { }
             public void Stop() { }
         }
    }
}
