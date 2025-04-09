//
// NullDhtEngine.cs
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


using MonoTorrent.BEncoding;

using System;
using System.Net; // Added for IPEndPoint
using System.Collections.Generic;
using System.Threading.Tasks; // Ensure Task is available

using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using MonoTorrent.PortForwarding; // Added for IPortForwarder

namespace MonoTorrent.Client
{
    class NullTransferMonitor : ITransferMonitor
    {
        public long BytesSent { get; }
        public long BytesReceived { get; }
        public long DownloadRate { get; }
        public long UploadRate { get; }
    }

    class NullDhtEngine : IDhtEngine
    {
#pragma warning disable 0067
        public event EventHandler<PeersFoundEventArgs> PeersFound {
            add { }
            remove { }
        }

        public event EventHandler StateChanged {
            add { }
            remove { }
        }
#pragma warning restore 0067

        public TimeSpan AnnounceInterval { get; }
        public bool Disposed => false;
        public TimeSpan MinimumAnnounceInterval { get; }
        public int NodeCount => 0;
        public ITransferMonitor Monitor { get; } = new NullTransferMonitor ();

        public NodeId LocalId => default; // Null engine doesn't have a real ID

        public DhtState State => DhtState.NotReady;
        public IPEndPoint? ExternalEndPoint { get; set; } // Added property

        public void Add (IEnumerable<ReadOnlyMemory<byte>> nodes)
        {

        }

        public void Announce (InfoHash infoHash, int port)
        {

        }

        public void Dispose ()
        {

        }

        public void GetPeers (InfoHash infoHash)
        {

        }


        public Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetAsync(NodeId target, long? sequenceNumber = null)
        {
            return Task.FromResult<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)>((null, null, null, null));
        }

        public Task<ReadOnlyMemory<byte>> SaveNodesAsync ()
        {
            return Task.FromResult (ReadOnlyMemory<byte>.Empty);
        }

        // Corrected SetListenerAsync
        public Task SetListenerAsync (IDhtListener listener)
        {
             return Task.CompletedTask;
        }

        // Correctly placed PutMutableAsync
        public Task PutMutableAsync(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null)
        {
            return Task.CompletedTask;
        }

        // Matches IDhtEngine
        public Task StartAsync (NatsNatTraversalService? natsService = null, MonoTorrent.PortForwarding.IPortForwarder? portForwarder = null)
        {
            // Null engine does nothing on start
            return Task.CompletedTask;
        }

        // Updated InitializeNatAsync signature
        public Task InitializeNatAsync (NatsNatTraversalService natsService, IPortForwarder? portForwarder)
        {
            // Null engine does nothing
            return Task.CompletedTask;
        }
 
        // Matches IDhtEngine
        public Task StartAsync (ReadOnlyMemory<byte> initialNodes, NatsNatTraversalService? natsService = null, MonoTorrent.PortForwarding.IPortForwarder? portForwarder = null)
        {
            // Null engine does nothing on start
            return Task.CompletedTask;
        }
 
        // Matches IDhtEngine
        public Task StartAsync (ReadOnlyMemory<byte> initialNodes, string[] bootstrapRouters, NatsNatTraversalService? natsService = null, MonoTorrent.PortForwarding.IPortForwarder? portForwarder = null)
        {
            // Null engine does nothing on start
            return Task.CompletedTask;
        }

        public Task StopAsync ()
        {
            return Task.CompletedTask;
        }
    }
}
