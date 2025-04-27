//
// FindNode.cs
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
using System.Collections.Generic; // Added for List<Node>
using System.Linq; // Added for Linq
 
namespace MonoTorrent.Dht.Messages
{
    sealed class FindNode : QueryMessage
    {
        static readonly BEncodedString TargetKey = new BEncodedString ("target");
        static readonly BEncodedString QueryName = new BEncodedString ("find_node");

        public NodeId Target => new NodeId ((BEncodedString) Parameters[TargetKey]);

        public FindNode (NodeId id, NodeId target)
            : base (id, QueryName)
        {
            Parameters.Add (TargetKey, BEncodedString.FromMemory (target.AsMemory ()));
        }

        public FindNode (BEncodedDictionary d)
            : base (d)
        {
        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            return new FindNodeResponse (parameters);
        }

        // Add 'bool receivedViaRelay' parameter
        public override void Handle (DhtEngine engine, Node node, bool receivedViaRelay)
        {
            // Base handle doesn't use the flag, but call it correctly
            base.Handle (engine, node, receivedViaRelay);

            // FindNode response always goes back directly via UDP.
            // Pass the engine's ExternalEndPoint (if available) to the response constructor
            var response = new FindNodeResponse (engine.RoutingTable.LocalNodeId, TransactionId!, engine.ExternalEndPoint);
 
            // Get the closest nodes from the routing table
            var closestNodes = engine.RoutingTable.GetClosest (Target);
 
            // If the target node itself was found, just return it (using ExternalEndPoint if it's the local node)
            Node? targetNode = engine.RoutingTable.FindNode (Target);
            if (targetNode != null) {
                if (targetNode.Id == engine.LocalId && engine.ExternalEndPoint != null) {
                    // Create a temporary node with the external endpoint for the response
                    var localNodeWithExternalEP = new Node(engine.LocalId, engine.ExternalEndPoint);
                    response.Nodes = localNodeWithExternalEP.CompactNode();
                } else {
                    response.Nodes = targetNode.CompactNode();
                }
            } else {
                // Prepare the list of nodes for the response, substituting the local node if necessary
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
                // Compact the potentially modified list of nodes
                response.Nodes = Node.CompactNode (responseNodes);
            }
 
            engine.MessageLoop.EnqueueSend (response, node, node.EndPoint);
        }
    }
}
