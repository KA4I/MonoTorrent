//
// GetRequest.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (implementing BEP44)
//
// Copyright (C) 2008 Alan McGovern
// Copyright (C) 2025 Roo
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
using System.Linq; // Needed for .Where()
using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    /// <summary>
    /// Represents a 'get' request in the DHT protocol (BEP44).
    /// Used for both immutable and mutable items.
    /// </summary>
    internal class GetRequest : QueryMessage
    {
        static readonly BEncodedString TargetKey = new BEncodedString ("target");
        static readonly BEncodedString SeqKey = new BEncodedString ("seq");
        static readonly BEncodedString QueryName = new BEncodedString ("get");

        public NodeId Target => new NodeId ((BEncodedString) Parameters[TargetKey]);

        public long? SequenceNumber {
            get {
                if (Parameters.TryGetValue (SeqKey, out BEncodedValue? val))
                    return ((BEncodedNumber) val).Number;
                return null;
            }
            private set {
                if (value.HasValue)
                    Parameters[SeqKey] = new BEncodedNumber (value.Value);
                else
                    Parameters.Remove (SeqKey);
            }
        }

        public GetRequest (NodeId id, NodeId target, long? sequenceNumber = null)
            : base (id, QueryName)
        {
            Parameters.Add (TargetKey, BEncodedString.FromMemory (target.AsMemory ()));
            SequenceNumber = sequenceNumber;
        }

        public GetRequest (BEncodedDictionary d)
            : base (d)
        {
        }

        public override ResponseMessage CreateResponse (BEncodedDictionary parameters)
        {
            // We need to create GetResponse.cs next
            // Note: This method seems unused if Handle sends the response directly.
            // Consider removing or revising its purpose. For now, return a base response.
            return new GetResponse (parameters);
        }

        public override void Handle(DhtEngine engine, Node node)
        {
            // Log entry into the Handle method
            System.Console.WriteLine($"[GetRequest.Handle {engine.LocalId.ToHex().Substring(0,6)}] Received GetRequest for Target {Target.ToHex().Substring(0,6)} from Node {node.Id.ToHex().Substring(0,6)} (EP: {node.EndPoint}). Requested Seq: {SequenceNumber?.ToString() ?? "N/A"}");
            base.Handle(engine, node);

            var response = new GetResponse(engine.RoutingTable.LocalNodeId, TransactionId!);
            response.Token = engine.TokenManager.GenerateToken(node);

            // Attempt to retrieve the item from the engine's actual local storage
            if (engine.TryGetStoredItem(Target, out StoredDhtItem? item))
            {
                // Item found locally
                if (item!.IsMutable) // Item null check handled by TryGetStoredItem
                {
                    // Mutable item found, check sequence number based on BEP44 rules
                    // Return value if stored seq >= requested seq, or if no seq was requested.
                    // BEP44: Respond with value+sig+seq if stored_seq > requested_seq, or if requested_seq is omitted.
                    if (!SequenceNumber.HasValue || item.SequenceNumber > SequenceNumber.Value)
                    {
                        Console.WriteLine($"[GetRequest.Handle] Found newer mutable item for {Target}. Stored Seq: {item.SequenceNumber}, Requested Seq: {SequenceNumber?.ToString() ?? "null"}. Populating response with value.");
                        response.Value = item.Value;
                        response.PublicKey = item.PublicKey; // Already checked IsMutable
                        response.Signature = item.Signature; // Already checked IsMutable
                        response.SequenceNumber = item.SequenceNumber; // Already checked IsMutable
                        // Note: BEP44 says nodes should not be included if value is present.
                    }
                    // BEP44: Respond with only seq if stored_seq <= requested_seq.
                    else // Covers item.SequenceNumber <= SequenceNumber.Value
                    {
                         Console.WriteLine($"[GetRequest.Handle] Found mutable item for {Target} but Stored Seq ({item.SequenceNumber}) <= Requested Seq ({SequenceNumber.Value}). Populating response with sequence number only.");
                         response.SequenceNumber = item.SequenceNumber;
                         // Do not include nodes or value/signature here, only the sequence number.
                    }
                }
                else
                {
                    // Immutable item found
                    // Note: Proper implementation should verify hash(item.Value) == Target
                    Console.WriteLine($"[GetRequest.Handle] Found stored immutable item for {Target}. Populating response.");
                    response.Value = item.Value;
                    // Note: BEP44 says nodes should not be included if value is present.
                }
            }
            else
            {
                // Item not found locally, return closer nodes
                Console.WriteLine($"[GetRequest.Handle] Item not found locally for {Target}. Finding and returning closest nodes.");
                var closestNodes = engine.RoutingTable.GetClosest(Target);
                response.Nodes = Node.CompactNode(closestNodes.Where(n => n.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList());
                response.Nodes6 = Node.CompactNode(closestNodes.Where(n => n.EndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList());
            }

            // Send the populated response
            engine.MessageLoop.EnqueueSend(response, node, node.EndPoint);
        }
    }
}
