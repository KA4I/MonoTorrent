//
// GetPeers.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
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


using MonoTorrent.BEncoding;
using MonoTorrent.Logging;
using System.Collections.Generic; // Added for List<Node>
using System.Linq; // Added for Linq
 
namespace MonoTorrent.Dht.Messages
{
    sealed class GetPeers : QueryMessage
    {
        static ILogger Logger = LoggerFactory.Create (nameof (GetPeers));

        static readonly BEncodedString InfoHashKey = new BEncodedString ("info_hash");
        static readonly BEncodedString QueryName = new BEncodedString ("get_peers");

        public NodeId InfoHash => new NodeId ((BEncodedString) Parameters[InfoHashKey]);

        public GetPeers (NodeId id, NodeId infohash)
            : base (id, QueryName)
        {
            Parameters.Add (InfoHashKey, BEncodedString.FromMemory (infohash.AsMemory ()));
        }

        public GetPeers (BEncodedDictionary d)
            : base (d)
        {

        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            return new GetPeersResponse (parameters);
        }

        public override void Handle (DhtEngine engine, Node node)
        {
            base.Handle (engine, node);

            if (TransactionId is null) {
                Logger.Error ("Transaction id was unexpectedly missing");
                return;
            }

            BEncodedString token = engine.TokenManager.GenerateToken (node);
            // Pass the engine's ExternalEndPoint (if available) to the response constructor
            var response = new GetPeersResponse (engine.RoutingTable.LocalNodeId, TransactionId, token, engine.ExternalEndPoint);
 
            if (engine.Torrents.ContainsKey (InfoHash)) {
                // This part sends compact peer info (IP/Port), not full node info.
                // It's unlikely the local node is in this list. If it were, we'd need
                // to ensure its ExternalEndPoint is used here too, but that seems unnecessary.
                var list = new BEncodedList ();
                foreach (Node n in engine.Torrents[InfoHash])
                    list.Add (n.CompactPort ());
                response.Values = list;
                // TODO: Add IPv6 values if available/needed (response.Values6)
            } else {
                // This part sends compact node info (ID/IP/Port).
                // We need to substitute the local node if it's present and ExternalEndPoint is known.
                var closestNodes = engine.RoutingTable.GetClosest (InfoHash);
                var responseNodes = new List<Node>(closestNodes.Count);
                foreach (var n in closestNodes)
                {
                    if (n.Id == engine.LocalId && engine.ExternalEndPoint != null)
                    {
                        // Substitute local node with one using the external endpoint
                        responseNodes.Add(new Node(engine.LocalId, engine.ExternalEndPoint));
                    }
                    else
                    {
                        responseNodes.Add(n);
                    }
                }
                // Compact the potentially modified list of nodes for IPv4 and IPv6 separately
                response.Nodes = Node.CompactNode (responseNodes.Where(n => n.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList());
                response.Nodes6 = Node.CompactNode (responseNodes.Where(n => n.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList());
            }
 
            engine.MessageLoop.EnqueueSend (response, node, node.EndPoint);
        }
    }
}
