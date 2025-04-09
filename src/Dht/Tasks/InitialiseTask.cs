//
// InitialiseTask.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
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


using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using MonoTorrent.Dht.Messages;

namespace MonoTorrent.Dht.Tasks
{
    class InitialiseTask
    {
        // Choose a completely arbitrary value here. If we have at least this many
        // nodes in the routing table we can consider it 'healthy' enough to allow
        // the state to change to 'Ready' so torrents can begin searching for peers
        const int MinHealthyNodes = 32;

        readonly List<Node> initialNodes;
        readonly DhtEngine engine;
        readonly TaskCompletionSource<object?> initializationComplete;

        static Node[]? BootstrapNodes { get; set; }
        string[] BootstrapRouters { get; set; }

        public InitialiseTask (DhtEngine engine)
            : this (engine, Enumerable.Empty<Node> (), DhtEngine.DefaultBootstrapRouters.ToArray ())
        {

        }

        public InitialiseTask (DhtEngine engine, IEnumerable<Node> nodes, string[] bootstrapRouters)
        {
            this.engine = engine;
            initialNodes = nodes.ToList ();
            BootstrapRouters = bootstrapRouters;
            initializationComplete = new TaskCompletionSource<object?> ();
        }

        public Task ExecuteAsync ()
        {
            DhtEngine.MainLoop.CheckThread ();
            BeginAsyncInit ();
            return initializationComplete.Task;
        }

        async void BeginAsyncInit ()
        {
            System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] BeginAsyncInit started.");
            // If we were given a list of nodes to load at the start, use them
            try {
                if (initialNodes.Count > 0) {
                    System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Using {initialNodes.Count} provided initial nodes.");
                    await SendFindNode (initialNodes);
                } else {
                    System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] No initial nodes provided. Attempting DNS resolution for bootstrap routers.");
                    try {
                        if (BootstrapNodes is null)
                            BootstrapNodes = await GenerateBootstrapNodes ();
                        System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] DNS resolution complete. Found {BootstrapNodes?.Length ?? 0} bootstrap nodes. Sending FindNode requests.");
                        await SendFindNode (BootstrapNodes);
                    } catch {
                        initializationComplete.TrySetResult (null);
                        return;
                    }
                }
            } finally {
                initializationComplete.TrySetResult (null);
            }
        }

        async Task<Node[]> GenerateBootstrapNodes ()
        {
            System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] GenerateBootstrapNodes started for {BootstrapRouters.Length} routers.");
            // Handle when one, or more, of the bootstrap nodes are offline
            var results = new List<Node> ();

            var tasks = BootstrapRouters.Select (Dns.GetHostAddressesAsync).ToList ();
            while (tasks.Count > 0) {
                var completed = await Task.WhenAny (tasks);
                tasks.Remove (completed);

                try {
                    var addresses = await completed;
                    foreach (var v in addresses)
                        results.Add (new Node (NodeId.Create (), new IPEndPoint (v, 6881)));
                        System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Resolved router to {string.Join(", ", addresses.Select(a => a.ToString()))}");
                } catch (Exception ex) {
                     System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] DNS resolution failed for a router: {ex.Message}");
                }
            }

            return results.ToArray ();
        }

        async Task SendFindNode (IEnumerable<Node> newNodes)
        {
            System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] SendFindNode started with {newNodes.Count()} initial nodes.");
            var activeRequests = new List<Task<SendQueryEventArgs>> ();
            var nodes = new ClosestNodesCollection (engine.LocalId);

            foreach (Node node in newNodes) {
                var request = new FindNode (engine.LocalId, engine.LocalId);
                System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Sending initial FindNode to {node.EndPoint}");
                activeRequests.Add (engine.SendQueryAsync (request, node));
                nodes.Add (node);
            }

            System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Entering FindNode loop. Active requests: {activeRequests.Count}");
            while (activeRequests.Count > 0) {
                var completed = await Task.WhenAny (activeRequests);
                System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Query completed.");
                activeRequests.Remove (completed);

                SendQueryEventArgs args = await completed;
                if (args.TimedOut) {
                     System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Query timed out for {args.EndPoint}.");
                }
                else if (args.Response != null) {
                    System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Received FindNode response from: {args.EndPoint}");
                    if (engine.RoutingTable.CountNodes () >= MinHealthyNodes)
                        initializationComplete.TrySetResult (null);

                    var response = (FindNodeResponse) args.Response;
                    int newFound = 0;
                    foreach (Node node in Node.FromCompactNode (response.Nodes)) {
                        if (nodes.Add (node)) {
                            newFound++;
                            var request = new FindNode (engine.LocalId, engine.LocalId);
                            System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Adding new FindNode query for {node.EndPoint}");
                            activeRequests.Add (engine.SendQueryAsync (request, node));
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Processed response. Found {newFound} new nodes. Active requests: {activeRequests.Count}");
                }
                else {
                     System.Diagnostics.Debug.WriteLine($"[DHT InitialiseTask {engine.LocalId}] Query failed or returned null response for {args.EndPoint}.");
                }
                
                }
            System.Diagnostics.Debug.WriteLine ($"[DHT InitialiseTask {engine.LocalId}] FindNode loop finished. Routing table node count: {engine.RoutingTable.CountNodes ()}");

            // If we started with specific initial nodes but the table still needs bootstrapping,
            // AND if the *original* call intended to use bootstrap routers (BootstrapRouters was not empty),
            // then fall back to using the routers. Otherwise, do not fall back.
            if (initialNodes.Count > 0 && engine.RoutingTable.NeedsBootstrap && this.BootstrapRouters.Length > 0) {
                System.Diagnostics.Debug.WriteLine ($"[DHT InitialiseTask {engine.LocalId}] Initial nodes processed, but table still needs bootstrap. Recursively calling InitialiseTask with configured routers ({this.BootstrapRouters.Length} routers).");
                // Pass the *same* bootstrap routers, not the defaults.
                await new InitialiseTask (engine, Array.Empty<Node>(), this.BootstrapRouters).ExecuteAsync ();
            } else if (initialNodes.Count > 0 && engine.RoutingTable.NeedsBootstrap) {
                 System.Diagnostics.Debug.WriteLine ($"[DHT InitialiseTask {engine.LocalId}] Initial nodes processed, but table still needs bootstrap. *Not* falling back to routers as none were provided initially.");
            }
        }
    }
}
