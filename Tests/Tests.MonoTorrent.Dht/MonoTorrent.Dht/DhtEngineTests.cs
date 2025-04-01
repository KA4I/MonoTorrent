using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;
using MonoTorrent.Dht.Tasks;
using NUnit.Framework;
using System.Security.Cryptography; // For SHA1

namespace MonoTorrent.Dht
{
    [TestFixture]
    public class DhtEngineTests
    {
        DhtEngine engine;
        TestListener listener;
        Node node;
        BEncodedString transactionId = "aa";

        [SetUp]
        public void Setup()
        {
            listener = new TestListener();
            engine = new DhtEngine();
            _ = engine.SetListenerAsync(listener);
            node = new Node(NodeId.Create(), new IPEndPoint(IPAddress.Parse("1.1.1.1"), 111));
            engine.RoutingTable.Add(node); // Add a node to route requests through
        }

        [TearDown]
        public async Task Teardown()
        {
            await engine.StopAsync();
            engine.Dispose();
            listener.Stop();
        }


        [Test]
        public void AddRawNodesBeforeStarting ()
        {
            int count = 0;
            var freshEngine = new DhtEngine ();
            freshEngine.MessageLoop.QuerySent += (o, e) => count++;
            freshEngine.Add (new ReadOnlyMemory<byte>[] { new byte[100] });
            Assert.AreEqual (0, freshEngine.MessageLoop.PendingQueries, "#1");
            Assert.AreEqual (0, count, "#2");
            Assert.AreEqual (0, freshEngine.RoutingTable.CountNodes (), "#3");
            Assert.AreEqual (0, freshEngine.MessageLoop.DhtMessageFactory.RegisteredMessages, "#4");
            freshEngine.Dispose();
        }

        [Test]
        public async Task Get_ArgumentNull()
        {
             Assert.ThrowsAsync<ArgumentNullException>(async () => await engine.GetAsync(null!), "#1");
        }

        [Test]
        public async Task PutImmutable_ArgumentNull()
        {
             Assert.ThrowsAsync<ArgumentNullException>(async () => await engine.PutImmutableAsync(null!), "#1");
        }

        [Test]
        public async Task PutMutable_ArgumentNull()
        {
            var pk = (BEncodedString)new byte[32];
            var sig = (BEncodedString)new byte[64];
            var val = (BEncodedString)"value";

            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync(null!, null, val, 1, sig), "#1 PK Null");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync((BEncodedString)new byte[31], null, val, 1, sig), "#2 PK Short");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync((BEncodedString)new byte[33], null, val, 1, sig), "#3 PK Long");
            Assert.ThrowsAsync<ArgumentNullException>(async () => await engine.PutMutableAsync(pk, null, null!, 1, sig), "#4 Value Null");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync(pk, null, val, 1, null!), "#5 Sig Null");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync(pk, null, val, 1, (BEncodedString)new byte[63]), "#6 Sig Short");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync(pk, null, val, 1, (BEncodedString)new byte[65]), "#7 Sig Long");
            Assert.ThrowsAsync<ArgumentException>(async () => await engine.PutMutableAsync(pk, (BEncodedString)new byte[65], val, 1, sig), "#8 Salt Long");
        }

        [Test]
        public void CalculateMutableTargetId_NoSalt()
        {
            var pkBytes = new byte[32];
            pkBytes.AsSpan().Fill(0xAB);
            var pk = (BEncodedString)pkBytes;
            //var expectedTarget = NodeId.FromHex("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709"); // This hex is SHA1 of empty string, not needed here.
            // Correct calculation: SHA1(pk)
            byte[] expectedBytes;
            using (var sha1 = SHA1.Create())
                expectedBytes = sha1.ComputeHash(pk.Span.ToArray());

            var target = DhtEngine.CalculateMutableTargetId(pk, null);
            Assert.AreEqual(new NodeId(expectedBytes), target, "#1");

            target = DhtEngine.CalculateMutableTargetId(pk, BEncodedString.Empty);
            Assert.AreEqual(new NodeId(expectedBytes), target, "#2 Empty Salt");
        }

        [Test]
        public void CalculateMutableTargetId_WithSalt()
        {
            var pkBytes = new byte[32];
            pkBytes.AsSpan().Fill(0xAB);
            var pk = (BEncodedString)pkBytes;
            var salt = (BEncodedString)"mySalt";

            // Correct calculation: SHA1(pk + salt)
            byte[] expectedBytes;
            using (var sha1 = SHA1.Create()) {
                 sha1.TransformBlock(pk.Span.ToArray(), 0, pk.Span.Length, null, 0);
                 sha1.TransformFinalBlock(salt.Span.ToArray(), 0, salt.Span.Length);
                 expectedBytes = sha1.Hash!;
            }

            var target = DhtEngine.CalculateMutableTargetId(pk, salt);
            Assert.AreEqual(new NodeId(expectedBytes), target, "#1");
        }

        // More involved tests requiring mocking GetPeers/Put responses would go here
        // or in IntegrationTests. For now, we've tested argument validation and
        // the target ID calculation. We assume GetTask/PutTask are created correctly.

    }
}
