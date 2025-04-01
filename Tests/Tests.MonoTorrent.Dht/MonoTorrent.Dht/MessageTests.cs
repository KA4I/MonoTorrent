//
// MessageTests.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2009 Alan McGovern
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
using System.Collections.Generic;
using System.Text;

using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;

using NUnit.Framework;
using System.Security.Cryptography; // For SHA1

namespace MonoTorrent.Dht
{
    [TestFixture]
    public class MessageTests
    {
        private readonly NodeId id = new NodeId (Encoding.UTF8.GetBytes ("abcdefghij0123456789"));
        private readonly NodeId infohash = new NodeId (Encoding.UTF8.GetBytes ("mnopqrstuvwxyz123456"));
        private readonly BEncodedString token = "aoeusnth";
        private readonly BEncodedString publicKey = new byte[32];
        private readonly BEncodedString salt = "mySalt";
        private readonly BEncodedString signature = new byte[64];
        private readonly BEncodedNumber sequenceNumber = 123;
        private readonly BEncodedString value = "myValue";
        private readonly BEncodedString transactionId = "aa";

        private QueryMessage message;
        DhtMessageFactory DhtMessageFactory;

        [SetUp]
        public void Setup ()
        {
            DhtMessage.UseVersionKey = false;
            DhtMessageFactory = new DhtMessageFactory ();
        }

        [TearDown]
        public void Teardown ()
        {
            DhtMessage.UseVersionKey = true;
        }

        #region Encode Tests

        [Test]
        public void AnnouncePeerEncode ()
        {
            Node n = new Node (NodeId.Create (), null);
            n.Token = token;
            AnnouncePeer m = new AnnouncePeer (id, infohash, 6881, token);
            m.TransactionId = transactionId;

            Compare (m, "d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz1234564:porti6881e5:token8:aoeusnthe1:q13:announce_peer1:t2:aa1:y1:qe");
        }

        [Test]
        public void AnnouncePeerResponseEncode ()
        {
            AnnouncePeerResponse m = new AnnouncePeerResponse (infohash, transactionId);

            Compare (m, "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re");
        }

        [Test]
        public void FindNodeEncode ()
        {
            FindNode m = new FindNode (id, infohash);
            m.TransactionId = transactionId;

            Compare (m, "d1:ad2:id20:abcdefghij01234567896:target20:mnopqrstuvwxyz123456e1:q9:find_node1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void FindNodeResponseEncode ()
        {
            FindNodeResponse m = new FindNodeResponse (id, transactionId);
            m.Nodes = "def456...";

            Compare (m, "d1:rd2:id20:abcdefghij01234567895:nodes9:def456...e1:t2:aa1:y1:re");
        }

        [Test]
        public void GetPeersEncode ()
        {
            GetPeers m = new GetPeers (id, infohash);
            m.TransactionId = transactionId;

            Compare (m, "d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz123456e1:q9:get_peers1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void GetPeersResponseEncode ()
        {
            GetPeersResponse m = new GetPeersResponse (id, transactionId, token);
            m.Values = new BEncodedList ();
            m.Values.Add ((BEncodedString) "axje.u");
            m.Values.Add ((BEncodedString) "idhtnm");
            Compare (m, "d1:rd2:id20:abcdefghij01234567895:token8:aoeusnth6:valuesl6:axje.u6:idhtnmee1:t2:aa1:y1:re");
        }

        [Test]
        public void PingEncode ()
        {
            Ping m = new Ping (id);
            m.TransactionId = transactionId;

            Compare (m, "d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void PingResponseEncode ()
        {
            PingResponse m = new PingResponse (infohash, transactionId);

            Compare (m, "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re");
        }

        [Test]
        public void GetEncode_Immutable()
        {
            GetRequest m = new GetRequest(id, infohash);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad2:id20:abcdefghij01234567896:target20:mnopqrstuvwxyz123456e1:q3:get1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void GetEncode_Mutable()
        {
            GetRequest m = new GetRequest(id, infohash, sequenceNumber.Number);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad2:id20:abcdefghij01234567893:seqi123e6:target20:mnopqrstuvwxyz123456e1:q3:get1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void GetResponseEncode_Value()
        {
            GetResponse m = new GetResponse(id, transactionId);
            m.Token = token;
            m.Value = value;
            Compare(m, "d1:rd2:id20:abcdefghij01234567895:token8:aoeusnth1:v7:myValuee1:t2:aa1:y1:re");
        }

         [Test]
        public void GetResponseEncode_Mutable()
        {
            GetResponse m = new GetResponse(id, transactionId);
            m.Token = token;
            m.Value = value;
            m.PublicKey = publicKey;
            m.SequenceNumber = sequenceNumber.Number;
            m.Signature = signature;
            Compare(m, "d1:rd2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\03:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:t2:aa1:y1:re");
        }

        [Test]
        public void GetResponseEncode_Nodes()
        {
            GetResponse m = new GetResponse(id, transactionId);
            m.Token = token;
            m.Nodes = "nodes";
            m.Nodes6 = "nodes6";
            Compare(m, "d1:rd2:id20:abcdefghij01234567895:nodes5:nodes6:nodes66:nodes65:token8:aoeusnthe1:t2:aa1:y1:re");
        }

        [Test]
        public void PutEncode_Immutable()
        {
            PutRequest m = new PutRequest(id, token, value);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad2:id20:abcdefghij01234567895:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void PutEncode_Mutable()
        {
            PutRequest m = new PutRequest(id, token, value, publicKey, null, sequenceNumber.Number, signature);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\03:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe");
            message = m;
        }

         [Test]
        public void PutEncode_Mutable_WithSalt()
        {
            PutRequest m = new PutRequest(id, token, value, publicKey, salt, sequenceNumber.Number, signature);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\04:salt6:mySalt3:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void PutEncode_Mutable_WithCas()
        {
            PutRequest m = new PutRequest(id, token, value, publicKey, null, sequenceNumber.Number, signature, 122);
            m.TransactionId = transactionId;
            Compare(m, "d1:ad3:casi122e2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\03:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe");
            message = m;
        }

        [Test]
        public void PutResponseEncode()
        {
            PutResponse m = new PutResponse(infohash, transactionId);
            Compare(m, "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re");
        }

        #endregion

        #region Decode Tests

        [Test]
        public void AnnouncePeerDecode ()
        {
            string text = "d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz1234564:porti6881e5:token8:aoeusnthe1:q13:announce_peer1:t2:aa1:y1:qe";
            AnnouncePeer m = (AnnouncePeer) Decode ("d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz1234564:porti6881e5:token8:aoeusnthe1:q13:announce_peer1:t2:aa1:y1:qe");
            Assert.AreEqual (m.TransactionId, transactionId, "#1");
            Assert.AreEqual (m.MessageType, QueryMessage.QueryType, "#2");
            Assert.AreEqual (id, m.Id, "#3");
            Assert.AreEqual (infohash, m.InfoHash, "#3");
            Assert.AreEqual ((BEncodedNumber) 6881, m.Port, "#4");
            Assert.AreEqual (token, m.Token, "#5");

            Compare (m, text);
            message = m;
        }


        [Test]
        public void AnnouncePeerResponseDecode ()
        {
            // Register the query as being sent so we can decode the response
            AnnouncePeerDecode ();
            DhtMessageFactory.RegisterSend (message);
            string text = "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re";

            AnnouncePeerResponse m = (AnnouncePeerResponse) Decode (text);
            Assert.AreEqual (infohash, m.Id, "#1");

            Compare (m, text);
        }

        [Test]
        public void FindNodeDecode ()
        {
            string text = "d1:ad2:id20:abcdefghij01234567896:target20:mnopqrstuvwxyz123456e1:q9:find_node1:t2:aa1:y1:qe";
            FindNode m = (FindNode) Decode (text);

            Assert.AreEqual (id, m.Id, "#1");
            Assert.AreEqual (infohash, m.Target, "#1");
            Compare (m, text);
        }

        [Test]
        public void FindNodeResponseDecode ()
        {
            FindNodeEncode ();
            DhtMessageFactory.RegisterSend (message);
            string text = "d1:rd2:id20:abcdefghij01234567895:nodes9:def456...e1:t2:aa1:y1:re";
            FindNodeResponse m = (FindNodeResponse) Decode (text);

            Assert.AreEqual (id, m.Id, "#1");
            Assert.AreEqual ((BEncodedString) "def456...", m.Nodes, "#2");
            Assert.AreEqual (transactionId, m.TransactionId, "#3");

            Compare (m, text);
        }

        [Test]
        public void GetPeersDecode ()
        {
            string text = "d1:ad2:id20:abcdefghij01234567899:info_hash20:mnopqrstuvwxyz123456e1:q9:get_peers1:t2:aa1:y1:qe";
            GetPeers m = (GetPeers) Decode (text);

            Assert.AreEqual (infohash, m.InfoHash, "#1");
            Assert.AreEqual (id, m.Id, "#2");
            Assert.AreEqual (transactionId, m.TransactionId, "#3");

            Compare (m, text);
        }

        [Test]
        public void GetPeersResponseDecode ()
        {
            GetPeersEncode ();
            DhtMessageFactory.RegisterSend (message);

            string text = "d1:rd2:id20:abcdefghij01234567895:token8:aoeusnth6:valuesl6:axje.u6:idhtnmee1:t2:aa1:y1:re";
            GetPeersResponse m = (GetPeersResponse) Decode (text);

            Assert.AreEqual (token, m.Token, "#1");
            Assert.AreEqual (id, m.Id, "#2");

            BEncodedList l = new BEncodedList ();
            l.Add ((BEncodedString) "axje.u");
            l.Add ((BEncodedString) "idhtnm");
            Assert.AreEqual (l, m.Values, "#3");

            Compare (m, text);
        }

        [Test]
        public void PingDecode ()
        {
            string text = "d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe";
            Ping m = (Ping) Decode (text);

            Assert.AreEqual (id, m.Id, "#1");

            Compare (m, text);
        }

        [Test]
        public void PingResponseDecode ()
        {
            PingEncode ();
            DhtMessageFactory.RegisterSend (message);

            string text = "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re";
            PingResponse m = (PingResponse) Decode (text);

            Assert.AreEqual (infohash, m.Id);

            Compare (m, "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re");
        }

        [Test]
        public void GetDecode_Immutable()
        {
            string text = "d1:ad2:id20:abcdefghij01234567896:target20:mnopqrstuvwxyz123456e1:q3:get1:t2:aa1:y1:qe";
            GetRequest m = (GetRequest)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(infohash, m.Target, "#2");
            Assert.IsNull(m.SequenceNumber, "#3");
            Assert.AreEqual(transactionId, m.TransactionId, "#4");
            Compare(m, text);
        }

        [Test]
        public void GetDecode_Mutable()
        {
            string text = "d1:ad2:id20:abcdefghij01234567893:seqi123e6:target20:mnopqrstuvwxyz123456e1:q3:get1:t2:aa1:y1:qe";
            GetRequest m = (GetRequest)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(infohash, m.Target, "#2");
            Assert.AreEqual(123L, m.SequenceNumber, "#3");
            Assert.AreEqual(transactionId, m.TransactionId, "#4");
            Compare(m, text);
        }

        [Test]
        public void GetResponseDecode_Value()
        {
            GetEncode_Immutable(); // Register the query
            DhtMessageFactory.RegisterSend(message);

            string text = "d1:rd2:id20:abcdefghij01234567895:token8:aoeusnth1:v7:myValuee1:t2:aa1:y1:re";
            GetResponse m = (GetResponse)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(token, m.Token, "#2");
            Assert.AreEqual(value, m.Value, "#3");
            Assert.IsNull(m.Nodes, "#4");
            Assert.IsNull(m.PublicKey, "#5");
            Assert.IsNull(m.SequenceNumber, "#6");
            Assert.IsNull(m.Signature, "#7");
            Compare(m, text);
        }

        [Test]
        public void GetResponseDecode_Mutable()
        {
            GetEncode_Mutable(); // Register the query
            DhtMessageFactory.RegisterSend(message);

            string text = "d1:rd2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\03:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:t2:aa1:y1:re";
            GetResponse m = (GetResponse)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(token, m.Token, "#2");
            Assert.AreEqual(value, m.Value, "#3");
            Assert.AreEqual(publicKey, m.PublicKey, "#4");
            Assert.AreEqual(sequenceNumber.Number, m.SequenceNumber, "#5");
            Assert.AreEqual(signature, m.Signature, "#6");
            Assert.IsNull(m.Nodes, "#7");
            Compare(m, text);
        }

        [Test]
        public void PutDecode_Immutable()
        {
            string text = "d1:ad2:id20:abcdefghij01234567895:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe";
            PutRequest m = (PutRequest)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(token, m.Token, "#2");
            Assert.AreEqual(value, m.Value, "#3");
            Assert.IsNull(m.PublicKey, "#4");
            Assert.IsNull(m.Salt, "#5");
            Assert.IsNull(m.SequenceNumber, "#6");
            Assert.IsNull(m.Signature, "#7");
            Assert.IsNull(m.Cas, "#8");
            Assert.AreEqual(transactionId, m.TransactionId, "#9");
            Compare(m, text);
        }

        [Test]
        public void PutDecode_Mutable()
        {
            string text = "d1:ad2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\03:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe";
            PutRequest m = (PutRequest)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(token, m.Token, "#2");
            Assert.AreEqual(value, m.Value, "#3");
            Assert.AreEqual(publicKey, m.PublicKey, "#4");
            Assert.IsNull(m.Salt, "#5");
            Assert.AreEqual(sequenceNumber.Number, m.SequenceNumber, "#6");
            Assert.AreEqual(signature, m.Signature, "#7");
            Assert.IsNull(m.Cas, "#8");
            Assert.AreEqual(transactionId, m.TransactionId, "#9");
            Compare(m, text);
        }

        [Test]
        public void PutDecode_Mutable_WithSalt()
        {
            string text = "d1:ad2:id20:abcdefghij01234567891:k32:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\04:salt6:mySalt3:seqi123e3:sig64:\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\05:token8:aoeusnth1:v7:myValuee1:q3:put1:t2:aa1:y1:qe";
            PutRequest m = (PutRequest)Decode(text);

            Assert.AreEqual(id, m.Id, "#1");
            Assert.AreEqual(token, m.Token, "#2");
            Assert.AreEqual(value, m.Value, "#3");
            Assert.AreEqual(publicKey, m.PublicKey, "#4");
            Assert.AreEqual(salt, m.Salt, "#5");
            Assert.AreEqual(sequenceNumber.Number, m.SequenceNumber, "#6");
            Assert.AreEqual(signature, m.Signature, "#7");
            Assert.IsNull(m.Cas, "#8");
            Assert.AreEqual(transactionId, m.TransactionId, "#9");
            Compare(m, text);
        }

        [Test]
        public void PutResponseDecode()
        {
            PutEncode_Immutable(); // Register the query
            DhtMessageFactory.RegisterSend(message);

            string text = "d1:rd2:id20:mnopqrstuvwxyz123456e1:t2:aa1:y1:re";
            PutResponse m = (PutResponse)Decode(text);

            Assert.AreEqual(infohash, m.Id, "#1");
            Assert.AreEqual(transactionId, m.TransactionId, "#2");
            Compare(m, text);
        }

        #endregion


        private void Compare (DhtMessage m, string expected)
        {
            ReadOnlyMemory<byte> b = m.Encode ();
            Assert.AreEqual (Encoding.UTF8.GetString (b.ToArray ()), expected);
        }

        private DhtMessage Decode (string p)
        {
            byte[] buffer = Encoding.UTF8.GetBytes (p);
            return DhtMessageFactory.DecodeMessage (BEncodedValue.Decode<BEncodedDictionary> (buffer));
        }
    }
}
