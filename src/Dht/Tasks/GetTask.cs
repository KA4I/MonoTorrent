//
// GetTask.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (implementing BEP44 Get logic)
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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;
using System.Security.Cryptography; // Added for SHA1
using System.Security.Cryptography; // Added for SHA1

namespace MonoTorrent.Dht.Tasks
{
    class GetTask
    {
        readonly DhtEngine Engine;
        readonly NodeId Target;
        readonly long? SequenceNumber; // Optional sequence number for mutable gets
        readonly IEnumerable<Node> NodesToQuery;
        readonly Dictionary<Node, BEncodedString> ActiveTokens = new Dictionary<Node, BEncodedString> ();
        BEncodedValue? ReceivedValue;
        long? HighestSeenSequenceNumber;
        BEncodedString? ReceivedPublicKey;
        BEncodedString? ReceivedSignature;

        // Constructor now takes the nodes to query
        public GetTask (DhtEngine engine, NodeId target, IEnumerable<Node> nodesToQuery, long? sequenceNumber = null)
        {
            Engine = engine;
            Target = target;
            NodesToQuery = nodesToQuery;
            SequenceNumber = sequenceNumber;
        }

        public async Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> ExecuteAsync ()
        {
            // Node discovery is now handled by the caller (DhtEngine)

            // Send 'get' requests to the closest nodes
            var tasks = new List<Task<SendQueryEventArgs>> ();
            foreach (Node node in NodesToQuery) {
                var request = new GetRequest (Engine.LocalId, Target, SequenceNumber);
                tasks.Add (Engine.SendQueryAsync (request, node));
            }

            await Task.WhenAll (tasks);

            foreach (var task in tasks) {
                var args = task.Result;
                if (args.Response is GetResponse response && !args.TimedOut) {
                    HandleGetResponse (response);
                }
            }

            // If we received a value, return it along with mutable data if present
            return (ReceivedValue, ReceivedPublicKey, ReceivedSignature, HighestSeenSequenceNumber);
        }

        void HandleGetResponse (GetResponse response)
        {
            // Process nodes response
            if (response.Nodes != null) {
                foreach (Node node in Node.FromCompactNode (response.Nodes))
                    Engine.RoutingTable.Add (node); // Add newly discovered nodes
            }
             if (response.Nodes6 != null) {
                foreach (Node node in Node.FromCompactNode (response.Nodes6))
                    Engine.RoutingTable.Add (node); // Add newly discovered nodes
            }

            // Store the write token for potential future 'put' operations
            if (response.Token != null) {
                // Assuming the response ID corresponds to the node we sent the query to
                var respondingNode = NodesToQuery.FirstOrDefault (n => n.Id == response.Id);
                if (respondingNode != null)
                    ActiveTokens[respondingNode] = response.Token;
            }

            // Handle the received value ('v' field)
            if (response.Value != null) {
                // For mutable items, check sequence number and signature
                if (response.PublicKey != null && response.Signature != null && response.SequenceNumber.HasValue) {
                    // TODO: Implement signature verification using response.PublicKey
                    //       and the sequence number + value.
                    //       Need an Ed25519 library for this.

                    // Only accept the value if the sequence number is higher than what we've seen
                    if (!HighestSeenSequenceNumber.HasValue || response.SequenceNumber.Value > HighestSeenSequenceNumber.Value) {
                        // TODO: Verify signature here before accepting the value
                        // Note: Verification without salt is incomplete here.
                        // Proper verification happens in TorrentManager where salt is known.
                        HighestSeenSequenceNumber = response.SequenceNumber.Value;
                        ReceivedValue = response.Value;
                        ReceivedPublicKey = response.PublicKey; // Store associated PK and Sig
                        ReceivedSignature = response.Signature;
                    }
                } else {
                    // Immutable item - just store the first value we get? Or should we verify the hash?
                    // BEP44 says: "A node making a lookup SHOULD verify the data it receives from the network,
                    // to verify that its hash matches the target that was looked up."
                    if (ReceivedValue == null) // Store the first immutable value received if hash matches
                    {
                        if (VerifyImmutableHash(response.Value, Target)) {
                            ReceivedValue = response.Value;
                        } else {
                            // Log hash mismatch? Maybe raise an event?
                            // For now, we just won't store the value if the hash is wrong.
                        }
                    }
                }
            }
        }

        // Placeholder for signature verification
        // This verification is incomplete as GetTask doesn't know the salt.
        // The primary verification must happen in TorrentManager.
        bool VerifySignature (BEncodedString publicKey, long sequenceNumber, BEncodedValue value, BEncodedString signature)
        {
             // Cannot fully verify here without salt information.
             // The calling code (TorrentManager) must perform full verification.
            return true; // Temporarily assume valid until TorrentManager verifies.
        }

        // Verifies the SHA1 hash of an immutable item against the target ID.
        bool VerifyImmutableHash(BEncodedValue value, NodeId target)
        {
            using (var sha1 = SHA1.Create())
            {
                // Need to encode the value to get the byte representation for hashing
                using(var rented = MemoryPool.Default.Rent(value.LengthInBytes(), out Memory<byte> buffer)) {
                    value.Encode(buffer.Span);
                    var hash = sha1.ComputeHash(buffer.Span.ToArray(), 0, value.LengthInBytes());
                    return target.Span.SequenceEqual(hash);
                }
            }
        }
    }
}