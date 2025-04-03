//
// ManualDhtEngine.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2019 Alan McGovern
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
using System.Collections.Generic;
using System.Threading.Tasks;

using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;

namespace MonoTorrent.Client
{
    public class ManualDhtEngine : IDhtEngine
    {
        public TimeSpan AnnounceInterval { get; }
        public bool Disposed { get; private set; }
        public TimeSpan MinimumAnnounceInterval { get; }
        public ITransferMonitor Monitor { get; }
        public int NodeCount => 0;
        public DhtState State { get; private set; }

        public event EventHandler<PeersFoundEventArgs> PeersFound;
        public event EventHandler StateChanged;

        public void Add (IEnumerable<ReadOnlyMemory<byte>> nodes)
        {

        }

        public void Announce (InfoHash infohash, int port)
        {

        }

        public void Dispose ()
            => Disposed = true;

        public void GetPeers (InfoHash infohash)
        {

        }



        // --- State for BEP46 Testing ---
        public BEncodedValue? CurrentMutableValue { get; set; }
        public BEncodedString? CurrentPublicKey { get; set; }
        public BEncodedString? CurrentSignature { get; set; }
        public BEncodedString? CurrentSalt { get; set; }
        public long CurrentSequenceNumber { get; set; }

        // BEP44/BEP46 functionality for testing
        internal Func<NodeId, long?, Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)>>? GetCallback { get; set; }

        public Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetAsync(NodeId target, long? sequenceNumber = null)
        {
            return GetCallback?.Invoke(target, sequenceNumber) 
                ?? Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((null, null, null, null));
        }


        // BEP44/BEP46 Put functionality for testing
        internal Func<BEncodedString, BEncodedString?, BEncodedValue, long, BEncodedString, long?, Task>? PutCallback { get; set; }

        public Task PutMutableAsync(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null)
        {
            return PutCallback?.Invoke(publicKey, salt, value, sequenceNumber, signature, cas) 
                ?? Task.CompletedTask;
        }

        public void RaisePeersFound (InfoHash infoHash, IList<PeerInfo> peers)
            => PeersFound?.Invoke (this, new PeersFoundEventArgs (infoHash, peers));

        public void RaiseStateChanged (DhtState newState)
        {
            State = newState;
            StateChanged?.Invoke (this, EventArgs.Empty);
        }

        public Task<ReadOnlyMemory<byte>> SaveNodesAsync ()
            => Task.FromResult (ReadOnlyMemory<byte>.Empty);

        public Task SetListenerAsync (IDhtListener listener)
        {
            return Task.CompletedTask;
        }

        public Task StartAsync ()
            => StartAsync (null);

        public Task StartAsync (ReadOnlyMemory<byte> initialNodes)
        {
            RaiseStateChanged (DhtState.Ready);
            return Task.CompletedTask;
        }

        public Task StopAsync ()
        {
            RaiseStateChanged (DhtState.NotReady);
            return Task.CompletedTask;
        }
    }
}
