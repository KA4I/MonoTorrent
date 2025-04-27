//
// MessageLoop.cs
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
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht.Messages;
using MonoTorrent.Logging;

using ReusableTasks;

namespace MonoTorrent.Dht
{
    class MessageLoop
    {
        // --- DHT RELAY SUPPORT ---
        // Allow relay-injected messages to be processed as if received from a node
        public void ProcessRelayMessage(byte[] payload, NodeId fromNodeId)
        {
            // Simulate receiving a UDP message from the given NodeId
            var node = Engine.RoutingTable.FindNode(fromNodeId);
            if (node != null && node.EndPoint != null)
            {
                ProcessRelayMessageRaw(payload, node.EndPoint);
            }
            else
            {
                // Try to resolve the real endpoint using NatsNatTraversalService if available
                System.Net.IPEndPoint endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.None, 0);
                var natsServiceProp = Engine.GetType().GetProperty("NatsService");
                object? natsService = natsServiceProp?.GetValue(Engine);
                if (natsService != null)
                {
                    var getPeersMethod = natsService.GetType().GetMethod("GetDiscoveredPeers");
                    if (getPeersMethod != null)
                    {
                        var peers = getPeersMethod.Invoke(natsService, null) as System.Collections.IDictionary;
                        if (peers != null && peers.Contains(fromNodeId))
                        {
                            endpoint = (System.Net.IPEndPoint)peers[fromNodeId];
                        }
                    }
                }
                node = new Node(fromNodeId, endpoint);
                Engine.RoutingTable.Add(node);
                System.Diagnostics.Debug.WriteLine($"[MessageLoop] Relay message: NodeId {fromNodeId.ToHex()} not found, adding with endpoint {endpoint}.");
                ProcessRelayMessageRaw(payload, node.EndPoint);
            }
        }

        // Internal helper to process a raw UDP message as if received from a given endpoint
        internal void ProcessRelayMessageRaw(byte[] payload, IPEndPoint endpoint)
        {
           // Call the private MessageReceived method
           // Simulate receiving via relay by calling MessageReceived with IsRelay = true
           MessageReceived(payload, endpoint, isRelay: true);
       }
        // --- END DHT RELAY SUPPORT ---
        static readonly ILogger Logger = LoggerFactory.Create (nameof (MessageLoop));

        struct SendDetails
        {
            public SendDetails (Node? node, IPEndPoint destination, DhtMessage message, TaskCompletionSource<SendQueryEventArgs>? tcs)
            {
                CompletionSource = tcs;
                Destination = destination;
                Node = node;
                Message = message;
                SentAt = new ValueStopwatch ();
            }
            public readonly TaskCompletionSource<SendQueryEventArgs>? CompletionSource;
            public readonly IPEndPoint Destination;
            public readonly DhtMessage Message;
            public readonly Node? Node;
            public ValueStopwatch SentAt;
        }

        internal event Action<object, SendQueryEventArgs>? QuerySent;

        internal DhtMessageFactory DhtMessageFactory { get; private set; }

        /// <summary>
        ///  The DHT engine which owns this message loop.
        /// </summary>
        DhtEngine Engine { get; }

        /// <summary>
        /// The listener instance which is used to send/receive messages.
        /// </summary>
        internal IDhtListener Listener { get; set; } // Made internal

        TransferMonitor Monitor { get; }

        /// <summary>
        /// The number of DHT messages which have been sent and no response has been received.
        /// </summary>
        internal int PendingQueries => WaitingResponse.Count;

        /// <summary>
        /// The list of messages which have been received from the attached IDhtListener which
        /// are waiting to be processed by the engine.
        /// </summary>
        Queue<ReceivedMessageDetails> ReceiveQueue { get; } // Use the new struct

        /// <summary>
        /// The list of messages which have been queued to send.
        /// </summary>
        Queue<SendDetails> SendQueue { get; }

        /// <summary>
        /// If a response is not received before the timeout expires, it will be cancelled.
        /// </summary>
        internal TimeSpan Timeout { get; set; }

        /// <summary>
        /// This is the list of messages which have been sent but no response (or error) has
        /// been received yet. The key for the dictionary is the TransactionId for the Query.
        /// </summary>
        Dictionary<BEncodedValue, SendDetails> WaitingResponse { get; }

        /// <summary>
        /// Temporary (re-usable) storage when cancelling timed out messages.
        /// </summary>
     List<SendDetails> WaitingResponseTimedOut { get; }

     // Struct to hold received message details including relay status
     private struct ReceivedMessageDetails
        {
            public IPEndPoint Source;
            public DhtMessage Message;
            public bool IsRelay; // True if received via NATS relay

            public ReceivedMessageDetails(IPEndPoint source, DhtMessage message, bool isRelay)
            {
                Source = source;
                Message = message;
                IsRelay = isRelay;
            }
        }

        public MessageLoop (DhtEngine engine, TransferMonitor monitor)
        {
            Engine = engine ?? throw new ArgumentNullException (nameof (engine));
            Monitor = monitor;
            DhtMessageFactory = new DhtMessageFactory ();
            Listener = new NullDhtListener ();
            ReceiveQueue = new Queue<ReceivedMessageDetails> (); // Use the new struct
            SendQueue = new Queue<SendDetails> ();
            Timeout = TimeSpan.FromSeconds (15);
            WaitingResponse = new Dictionary<BEncodedValue, SendDetails> ();
            WaitingResponseTimedOut = new List<SendDetails> ();

            Task? sendTask = null;
            DhtEngine.MainLoop.QueueTimeout (TimeSpan.FromMilliseconds (5), () => {
                monitor.ReceiveMonitor.Tick ();
                monitor.SendMonitor.Tick ();

                if (engine.Disposed)
                    return false;
                try {
                    if (sendTask == null || sendTask.IsCompleted)
                        sendTask = SendMessages ();

                    while (ReceiveQueue.Count > 0)
                        ReceiveMessage ();

                    TimeoutMessages ();
                } catch (Exception ex) {
                    Debug.WriteLine ("Error in DHT main loop:");
                    Debug.WriteLine (ex);
                }

                return !engine.Disposed;
            });
        }

     async void MessageReceived (ReadOnlyMemory<byte> buffer, IPEndPoint endpoint, bool isRelay = false)
     {
         await DhtEngine.MainLoop;

            // Don't handle new messages if we have already stopped the dht engine.
            if (Listener.Status == ListenerStatus.NotListening)
                return;

            // I should check the IP address matches as well as the transaction id
            // FIXME: This should throw an exception if the message doesn't exist, we need to handle this
            // and return an error message (if that's what the spec allows)
            try {
                // Console.WriteLine($"[MessageLoop] Received {buffer.Length} bytes from {endpoint}"); // Log received data
                BEncodedValue decodedValue = BEncodedValue.Decode(buffer.Span, false);
                if (DhtMessageFactory.TryDecodeMessage ((BEncodedDictionary) decodedValue, out DhtMessage? message)) {
                  // Console.WriteLine($"[MessageLoop] Successfully decoded message from {endpoint}: {message.GetType().Name}"); // Log successful decode (Commented out)
                 Monitor.ReceiveMonitor.AddDelta (buffer.Length);
                 ReceiveQueue.Enqueue (new ReceivedMessageDetails (endpoint, message!, isRelay)); // Enqueue with relay status
             } else {
                  Console.WriteLine($"[MessageLoop] Failed to decode message from {endpoint}. Decoded value: {decodedValue}"); // Log decode failure
                }
            } catch (MessageException ex) {
                 Console.WriteLine($"[MessageLoop] MessageException decoding message from {endpoint}: {ex.Message}"); // Log MessageException
                // Caused by bad transaction id usually - ignore
            } catch (Exception ex) {
                 Console.WriteLine($"[MessageLoop] Exception decoding message from {endpoint}: {ex.Message}"); // Log other exceptions
                //throw new Exception("IP:" + endpoint.Address.ToString() + "bad transaction:" + e.Message);
            }
        }

        void RaiseMessageSent (Node node, IPEndPoint endpoint, QueryMessage query)
        {
            QuerySent?.Invoke (this, new SendQueryEventArgs (node, endpoint, query));
        }

        void RaiseMessageSent (Node node, IPEndPoint endpoint, QueryMessage query, ResponseMessage response)
        {
            QuerySent?.Invoke (this, new SendQueryEventArgs (node, endpoint, query, response));
        }

        void RaiseMessageSent (Node node, IPEndPoint endpoint, QueryMessage query, ErrorMessage error)
        {
            QuerySent?.Invoke (this, new SendQueryEventArgs (node, endpoint, query, error));
        }

        async Task SendMessages ()
        {
            for (int i = 0; i < 5 && SendQueue.Count > 0; i++) {
                SendDetails details = SendQueue.Dequeue ();

                details.SentAt = ValueStopwatch.StartNew ();
                if (details.Message is QueryMessage) {
                    if (details.Message.TransactionId is null) {
                        Logger.Error ("Transaction id was unexpectedly missing while sending messages");
                        return;
                    }
                    WaitingResponse.Add (details.Message.TransactionId, details);
                }

                ReadOnlyMemory<byte> buffer = details.Message.Encode ();
                try {
                    Monitor.SendMonitor.AddDelta (buffer.Length);
                    await Listener.SendAsync (buffer, details.Destination);
                } catch {
                    TimeoutMessage (details);
                }
            }
        }

        internal void Start ()
        {
            DhtEngine.MainLoop.CheckThread ();

            DhtMessageFactory = new DhtMessageFactory ();
            if (Listener.Status != ListenerStatus.Listening)
                Listener.Start ();
        }

        internal void Stop ()
        {
            DhtEngine.MainLoop.CheckThread ();

            DhtMessageFactory = new DhtMessageFactory ();
            SendQueue.Clear ();
            ReceiveQueue.Clear ();
            WaitingResponse.Clear ();
            WaitingResponseTimedOut.Clear ();

            if (Listener.Status != ListenerStatus.NotListening)
                Listener.Stop ();
        }

        void TimeoutMessages ()
        {
            DhtEngine.MainLoop.CheckThread ();

            foreach (KeyValuePair<BEncodedValue, SendDetails> v in WaitingResponse) {
                if (Timeout == TimeSpan.Zero || v.Value.SentAt.Elapsed > Timeout)
                    WaitingResponseTimedOut.Add (v.Value);
            }

            foreach (SendDetails v in WaitingResponseTimedOut)
                TimeoutMessage (v);

            WaitingResponseTimedOut.Clear ();
        }

        void TimeoutMessage (SendDetails v)
        {
            DhtEngine.MainLoop.CheckThread ();

            DhtMessageFactory.UnregisterSend ((QueryMessage) v.Message);
            WaitingResponse.Remove (v.Message.TransactionId!);

            v.CompletionSource?.TrySetResult (new SendQueryEventArgs (v.Node!, v.Destination, (QueryMessage) v.Message));
            RaiseMessageSent (v.Node!, v.Destination, (QueryMessage) v.Message);
        }

        void ReceiveMessage ()
        {
            DhtEngine.MainLoop.CheckThread ();
 
            ReceivedMessageDetails receivedDetails = ReceiveQueue.Dequeue (); // Dequeue the struct
            DhtMessage rawResponse = receivedDetails.Message;
            IPEndPoint source = receivedDetails.Source;
            SendDetails query = default;
            bool receivedViaRelay = receivedDetails.IsRelay; // Get the relay status

            // What to do if the transaction id is empty?
            BEncodedValue? responseTransactionId = rawResponse.TransactionId;
            if (responseTransactionId is null) {
                Logger.Error ("Received a Dht response with no transaction id");
                return;
            }

            try {
                Node? node = Engine.RoutingTable.FindNode (rawResponse.Id);
                bool nodeJustAdded = false;
                if (node == null) {
                    var newNode = new Node (rawResponse.Id, source);
                    // Add the node first
                    Engine.RoutingTable.Add (newNode);
                    // Then try to find it again immediately to ensure it was added and we use the table's instance
                    node = Engine.RoutingTable.FindNode (rawResponse.Id);
                    if (node == null) {
                         // Log an error if the node wasn't found immediately after adding.
                         // This indicates a potential issue in RoutingTable.Add or FindNode.
                         Logger.Error ($"Node {rawResponse.Id.ToHex ()} from {source} was not found immediately after adding to routing table. Message handling aborted.");
                         return; // Cannot proceed if node wasn't added/found
                    }
                    // Note: 'node' now refers to the instance retrieved from the table (or the originally found one).
                }

                // If we have received a ResponseMessage corresponding to a query we sent, we should
                // remove it from our list before handling it as that could cause an exception to be
                // thrown.
                if (rawResponse is ResponseMessage || rawResponse is ErrorMessage) {
                    if (!WaitingResponse.TryGetValue (responseTransactionId, out query))
                        return;
                    WaitingResponse.Remove (responseTransactionId);
                }

                node.Seen ();
                if (rawResponse is ResponseMessage responseMessage) { // Renamed 'response' to 'responseMessage'
                    QueryMessage? queryMessage = query.Message as QueryMessage;
                    if (queryMessage is null) {
                        Logger.Error ("Received a dht response but the corresponding query message was missing");
                        return;
                    }

                    // Pass the relay status to the response handler as well
                    responseMessage.Handle (Engine, node, receivedViaRelay);
                    // Use the original node from the query details, not the node derived from the response sender's ID/endpoint
                    query.CompletionSource?.TrySetResult (new SendQueryEventArgs (query.Node!, query.Destination, queryMessage, responseMessage));
                    RaiseMessageSent (query.Node!, query.Destination, queryMessage, responseMessage);
                } else if (rawResponse is ErrorMessage error) {
                    QueryMessage? queryMessage = query.Message as QueryMessage;
                    if (queryMessage is null) {
                        Logger.Error ("Received a dht response but the corresponding query message was missing");
                        return;
                    }

                    // Use the original node from the query details for error reporting too
                    query.CompletionSource?.TrySetResult (new SendQueryEventArgs (query.Node!, query.Destination, queryMessage, error));
                    RaiseMessageSent (query.Node!, query.Destination, queryMessage, error);
                } else if (rawResponse is QueryMessage queryMessage) {
                    // Handle incoming queries
                    Console.WriteLine($"[MessageLoop] Received Query: {queryMessage.GetType().Name} from {source}"); // Log incoming query
                    // Call Handle, assuming it sends the response internally.
                // Pass relay context to Handle method (needs modification in DhtMessage subclasses)
                queryMessage.Handle(Engine, node, receivedViaRelay); // Pass the relay status
            } else {
                // Log unexpected message types
                     Console.WriteLine ($"[MessageLoop] Error: Unknown message type received: {rawResponse.GetType().Name}");
                }
            } catch (MessageException ex) {
                // Log message exceptions
                Console.WriteLine ($"[MessageLoop] Error: MessageException handling message from {source}: {ex.Message}");
                // Attempt to send a generic error response if we can identify the original query
                if (query.Message != null && query.Message is QueryMessage originalQuery) { // Ensure query.Message is QueryMessage
                     var error = new ErrorMessage (responseTransactionId, ErrorCode.GenericError, "Unexpected error processing message");
                     query.CompletionSource?.TrySetResult (new SendQueryEventArgs (query.Node!, query.Destination!, originalQuery, error));
                     EnqueueSend (error, null, source);
                }
            } catch (Exception ex) {
                 // Log other exceptions
                 Console.WriteLine ($"[MessageLoop] Error: Exception handling message from {source}: {ex.Message}");
                 // Attempt to send a generic error response if we can identify the original query
                 if (query.Message != null && query.Message is QueryMessage originalQuery) { // Ensure query.Message is QueryMessage
                     var error = new ErrorMessage (responseTransactionId, ErrorCode.GenericError, "Unexpected exception processing message");
                     query.CompletionSource?.TrySetResult (new SendQueryEventArgs (query.Node!, query.Destination!, originalQuery, error));
                     EnqueueSend (error, null, source);
                 }
            }
        }
// Wrapper method to match the expected event signature
private void Listener_MessageReceived (ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
{
    // Call the internal method, assuming direct UDP receive (not via relay)
    MessageReceived (buffer, endpoint, isRelay: false);
}

internal ReusableTask SetListener (IDhtListener listener)
{
    DhtEngine.MainLoop.CheckThread ();

    // Use the wrapper method for subscribing/unsubscribing
    Listener.MessageReceived -= Listener_MessageReceived;
    Listener = listener ?? new NullDhtListener ();
    Listener.MessageReceived += Listener_MessageReceived;
    return ReusableTask.CompletedTask;
        }

        internal void EnqueueSend (DhtMessage message, Node? node, IPEndPoint endpoint, TaskCompletionSource<SendQueryEventArgs>? tcs = null)
        {
            DhtEngine.MainLoop.CheckThread ();

            if (message.TransactionId == null) {
                if (message is ResponseMessage)
                    throw new ArgumentException ("Message must have a transaction id");
                do {
                    message.TransactionId = TransactionId.NextId ();
                } while (DhtMessageFactory.IsRegistered (message.TransactionId));
            }

            // We need to be able to cancel a query message if we time out waiting for a response
            if (message is QueryMessage)
                DhtMessageFactory.RegisterSend ((QueryMessage) message);

            SendQueue.Enqueue (new SendDetails (node, endpoint, message, tcs));
        }

        internal void EnqueueSend (DhtMessage message, Node node, TaskCompletionSource<SendQueryEventArgs>? tcs = null)
        {
            DhtEngine.MainLoop.CheckThread ();

            EnqueueSend (message, node, node.EndPoint, tcs);
        }

        public Task<SendQueryEventArgs> SendAsync (DhtMessage message, Node node)
        {
            DhtEngine.MainLoop.CheckThread ();

            var tcs = new TaskCompletionSource<SendQueryEventArgs> ();
            EnqueueSend (message, node, tcs);
            return tcs.Task;
        }
    }
}
