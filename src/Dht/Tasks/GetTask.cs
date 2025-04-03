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
        long? HighestSeenSequenceNumberFromResponseOnly; // Track seq num even without value
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
            Console.WriteLine($"[GetTask {Target}] ExecuteAsync started. Querying {NodesToQuery.Count()} nodes. Requested Seq: {SequenceNumber?.ToString() ?? "null"}"); // Log entry
            // Node discovery is now handled by the caller (DhtEngine)

            // Send 'get' requests to the closest nodes
            var tasks = new List<Task<SendQueryEventArgs>> ();
            int queryIndex = 0;
            foreach (Node node in NodesToQuery) {
                Console.WriteLine($"[GetTask {Target}] Sending GetRequest to Node {queryIndex++}: {node.Id} ({node.EndPoint})"); // Log node being queried
                var request = new GetRequest (Engine.LocalId, Target, SequenceNumber);
                tasks.Add (Engine.SendQueryAsync (request, node));
            }

            Console.WriteLine($"[GetTask {Target}] Waiting for {tasks.Count} queries to complete...");
            await Task.WhenAll (tasks);
            Console.WriteLine($"[GetTask {Target}] All queries completed.");

            int responseCount = 0;
            int timeoutCount = 0;
            foreach (var task in tasks) {
                var args = task.Result;
                if (args.TimedOut) {
                    timeoutCount++;
                    Console.WriteLine($"[GetTask {Target}] Query to Node {args.Node.Id} timed out.");
                } else if (args.Response is GetResponse response) {
                    responseCount++;
                    Console.WriteLine($"[GetTask {Target}] Received GetResponse from Node {response.Id}.");
                    HandleGetResponse (response);
                } else {
                     Console.WriteLine($"[GetTask {Target}] Received unexpected response type from Node {args.Node.Id}: {args.Response?.GetType().Name ?? "null"}");
                }
            }
            Console.WriteLine($"[GetTask {Target}] Processed results: {responseCount} responses, {timeoutCount} timeouts.");

            // If we received a value, return it along with mutable data if present
            // Prioritize returning the actual value if found.
            if (ReceivedValue != null) {
                 Console.WriteLine ($"[GetTask {Target}] ExecuteAsync finished. Returning: ValuePresent=True, PKPresent={ReceivedPublicKey != null}, SigPresent={ReceivedSignature != null}, HighestSeq={HighestSeenSequenceNumber}");
                 // Ensure HighestSeenSequenceNumber reflects the sequence associated with ReceivedValue
                 return (ReceivedValue, ReceivedPublicKey, ReceivedSignature, HighestSeenSequenceNumber);
            } else if (HighestSeenSequenceNumberFromResponseOnly.HasValue) {
                 // If no value was found, but we did see a sequence number in a response, return that sequence number.
                 Console.WriteLine ($"[GetTask {Target}] ExecuteAsync finished. No value found, but returning highest seen sequence number: ValuePresent=False, PKPresent=False, SigPresent=False, HighestSeq={HighestSeenSequenceNumberFromResponseOnly}");
                 return (null, null, null, HighestSeenSequenceNumberFromResponseOnly);
            } else {
                 // If neither value nor sequence number was found in any response.
                 Console.WriteLine ($"[GetTask {Target}] ExecuteAsync finished. Returning: ValuePresent=False, PKPresent=False, SigPresent=False, HighestSeq=null");
                 return (null, null, null, null);
            }
        }

        void HandleGetResponse (GetResponse response)
        {
            Console.WriteLine($"[GetTask {Target}] Handling response from Node {response.Id}"); // Log entry

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
                 Console.WriteLine($"[GetTask {Target}] Response from {response.Id} contains value.");
                // For mutable items, check sequence number and signature
                if (response.PublicKey != null && response.Signature != null && response.SequenceNumber.HasValue) {
                    long receivedSeq = response.SequenceNumber.Value;
                    long requestedMinSeq = SequenceNumber ?? -1;
                    long? currentHighestSeq = HighestSeenSequenceNumber;
                    Console.WriteLine($"[GetTask {Target}] Mutable item from {response.Id}. Received Seq: {receivedSeq}, Requested Min: {requestedMinSeq}, Current Highest: {currentHighestSeq?.ToString() ?? "null"}");

                    // Check if the response sequence number is greater than the minimum requested *and* greater than the highest seen so far in this task.
                    bool seqCheckPassed = receivedSeq > requestedMinSeq &&
                                          (!currentHighestSeq.HasValue || receivedSeq > currentHighestSeq.Value);

                    Console.WriteLine($"[GetTask {Target}] Sequence check result for {response.Id}: {seqCheckPassed}");

                    if (seqCheckPassed) {
                        // Signature verification happens in TorrentManager where salt is known.
                        Console.WriteLine($"[GetTask {Target}] Accepting value from {response.Id}. Updating HighestSeenSequenceNumber to {receivedSeq}.");
                        HighestSeenSequenceNumber = receivedSeq;
                        ReceivedValue = response.Value;
                        ReceivedPublicKey = response.PublicKey; // Store associated PK and Sig
                        ReceivedSignature = response.Signature;
                    } else {
                         if (receivedSeq <= requestedMinSeq) {
                             Console.WriteLine($"[GetTask {Target}] Discarding value from {response.Id}: Received Seq ({receivedSeq}) <= Requested Min ({requestedMinSeq}).");
                         } else if (currentHighestSeq.HasValue && receivedSeq <= currentHighestSeq.Value) {
                             Console.WriteLine($"[GetTask {Target}] Discarding value from {response.Id}: Received Seq ({receivedSeq}) <= Current Highest ({currentHighestSeq.Value}).");
                         }
                    }
                } else {
                    Console.WriteLine($"[GetTask {Target}] Immutable item from {response.Id}.");
                    // Immutable item - just store the first value we get? Or should we verify the hash?
                    // BEP44 says: "A node making a lookup SHOULD verify the data it receives from the network,
                    // to verify that its hash matches the target that was looked up."
                    if (ReceivedValue == null) // Store the first immutable value received if hash matches
                    {
                        bool hashValid = VerifyImmutableHash(response.Value, Target);
                        Console.WriteLine($"[GetTask {Target}] Immutable hash verification result for {response.Id}: {hashValid}");
                        if (hashValid) {
                            Console.WriteLine($"[GetTask {Target}] Accepting immutable value from {response.Id}.");
                            ReceivedValue = response.Value;
                        } else {
                            Console.WriteLine($"[GetTask {Target}] Discarding immutable value from {response.Id}: Hash mismatch.");
                            // Log hash mismatch? Maybe raise an event?
                            // For now, we just won't store the value if the hash is wrong.
                        }
                    } else {
                         Console.WriteLine($"[GetTask {Target}] Discarding immutable value from {response.Id}: Already have a value.");
                    }
                }
            } else { // response.Value == null
                 Console.WriteLine($"[GetTask {Target}] Response from {response.Id} does not contain value.");
                 if (response.SequenceNumber.HasValue) {
                     // BEP44: Handle case where only sequence number is returned (seq_req >= seq_stored)
                     HighestSeenSequenceNumberFromResponseOnly = Math.Max(HighestSeenSequenceNumberFromResponseOnly ?? -1, response.SequenceNumber.Value);
                     Console.WriteLine ($"[GetTask {Target}] Received sequence number {response.SequenceNumber.Value} without value from {response.Id}. Updating HighestSeenSequenceNumberFromResponseOnly to {HighestSeenSequenceNumberFromResponseOnly}.");
                 }
                 // If we didn't get a value, process the nodes included in the response (if any).
                 // This covers cases where only nodes were returned, or where only seq+nodes were returned.
                 AddClosestNodes (response.Nodes);
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
        } // End of VerifyImmutableHash
    
        void AddClosestNodes (BEncodedString? nodes)
        {
            if (nodes == null)
                return;

            var node = Node.FromCompactNode (nodes);
            foreach (var n in node)
                Engine.RoutingTable.Add (n);
        }
    } // End of GetTask class
        // Removed duplicate AddClosestNodes method and extra brace
}