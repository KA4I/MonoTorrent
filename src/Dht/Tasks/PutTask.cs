//
// PutTask.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (implementing BEP44 Put logic)
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
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Dht.Messages;

namespace MonoTorrent.Dht.Tasks
{
    /// <summary>
    /// Task to handle sending 'put' messages to the DHT for storing data (BEP44).
    /// </summary>
    class PutTask
    {
        readonly DhtEngine Engine;
        readonly PutRequest Request;
        readonly Dictionary<Node, BEncodedString> NodesWithTokens; // Nodes we got a write token from

        // Constructor for Immutable items
        public PutTask (DhtEngine engine, BEncodedValue value, Dictionary<Node, BEncodedString> nodesWithTokens)
        {
            Engine = engine;
            NodesWithTokens = nodesWithTokens;
            // Note: We need a token per node, so the PutRequest needs to be created per node inside ExecuteAsync
            // We store the value here and create the specific request later.
            // This constructor signature might need adjustment based on how tokens are managed.
            // For now, let's assume a simplified approach where the request object holds the value,
            // and we iterate through nodes+tokens.
             Request = new PutRequest(engine.LocalId, BEncodedString.Empty, value); // Token is placeholder
        }

        // Constructor for Mutable items
        public PutTask (DhtEngine engine, BEncodedValue value, BEncodedString publicKey, BEncodedString? salt, long sequenceNumber, BEncodedString signature, long? cas, Dictionary<Node, BEncodedString> nodesWithTokens)
        {
             Engine = engine;
             NodesWithTokens = nodesWithTokens;
             // Similar to immutable, token is per-node. Store mutable params and create request later.
             Request = new PutRequest(engine.LocalId, BEncodedString.Empty, value, publicKey, salt, sequenceNumber, signature, cas); // Token is placeholder
        }


        public async Task ExecuteAsync ()
        {
            var tasks = new List<Task<SendQueryEventArgs>> ();

            foreach (var kvp in NodesWithTokens) {
                Node node = kvp.Key;
                BEncodedString token = kvp.Value;

                PutRequest nodeSpecificRequest;
                if (Request.PublicKey != null && Request.Signature != null && Request.SequenceNumber.HasValue) {
                    // Mutable Put
                    nodeSpecificRequest = new PutRequest (
                        Engine.LocalId,
                        token,
                        Request.Value,
                        Request.PublicKey,
                        Request.Salt,
                        Request.SequenceNumber.Value,
                        Request.Signature,
                        Request.Cas
                    );
                } else {
                    // Immutable Put
                    nodeSpecificRequest = new PutRequest (
                        Engine.LocalId,
                        token,
                        Request.Value
                    );
                }
                // Set the transaction ID here or let SendQueryAsync handle it?
                // Let SendQueryAsync handle it for now.

                tasks.Add (Engine.SendQueryAsync (nodeSpecificRequest, node));
            }

            // Wait for all tasks to complete, but with an overall timeout
            // to prevent one slow node from blocking the entire operation excessively.
            // Handle empty list case to avoid issues with Task.WhenAll
            var whenAllTask = tasks.Count > 0 ? System.Threading.Tasks.Task.WhenAll(tasks) : System.Threading.Tasks.Task.CompletedTask;
            var overallTimeout = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5)); // 5 second timeout
            var completedInnerTask = await System.Threading.Tasks.Task.WhenAny(whenAllTask, overallTimeout);

            if (completedInnerTask == overallTimeout)
            {
                System.Diagnostics.Debug.WriteLine($"[DHT PutTask WARNING] Overall timeout reached after 5 seconds. Put may not have completed on all nodes.");
                // We don't know which specific tasks timed out here without more complex tracking,
                // but we allow the flow to continue. The Task.WhenAll call continues in the background.
            }
            else
            {
                // whenAllTask completed within the timeout.
                // Await it directly to ensure completion and propagate any aggregate exceptions.
                try
                {
                    await whenAllTask; // Await the original Task.WhenAll
                    System.Diagnostics.Debug.WriteLine($"[DHT PutTask] Task.WhenAll completed successfully within timeout.");
                }
                catch (Exception ex)
                {
                    // This catches aggregate exceptions if any task within WhenAll failed.
                    System.Diagnostics.Debug.WriteLine($"[DHT PutTask ERROR] Exception during Task.WhenAll: {ex.Message}");
                    // You might want to log ex.InnerExceptions if it's an AggregateException
                }
            }

            // Process responses (mostly just checking for errors)
            foreach (var task in tasks) {
                var args = task.Result;
                if (args.TimedOut) {
                    // Handle timeout for this specific node
                } else if (args.Response is PutResponse response) {
                    // Successfully put data to this node
                } else if (args.Error is ErrorMessage error) {
                    // Handle error from this specific node (e.g., bad token, seq too low)
                    // BEP44 Error codes:
                    // 205: message (v field) too big
                    // 206: invalid signature
                    // 207: salt too big
                    // 301: CAS hash mismatched
                    // 302: sequence number less than current
                }
            }
            // This task doesn't really return anything, it just attempts the put.
        }
    }
}