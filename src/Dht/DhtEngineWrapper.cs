//
// DhtEngineWrapper.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2022 Alan McGovern
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
using System.Threading.Tasks;
using MonoTorrent.BEncoding;


using MonoTorrent.Dht;

namespace MonoTorrent.Client
{
    class DhtEngineWrapper : IDht
    {
        IDhtEngine Engine { get; }

        public event EventHandler? StateChanged;

        public ITransferMonitor Monitor => Engine.Monitor;
        public int NodeCount => Engine.NodeCount;
        public DhtState State => Engine.State;

        public TimeSpan AnnounceInterval => Engine.AnnounceInterval;
        public TimeSpan MinimumAnnounceInterval => Engine.MinimumAnnounceInterval;

        public DhtEngineWrapper (IDhtEngine engine)
        {
            Engine = engine;
            Engine.StateChanged += (o, e) => StateChanged?.Invoke (this, EventArgs.Empty);
        }

        public Task PutMutableAsync(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null)
        {
            return Engine.PutMutableAsync(publicKey, salt, value, sequenceNumber, signature, cas);
        }

        public Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetAsync(NodeId target, long? sequenceNumber = null)
        {
            return Engine.GetAsync(target, sequenceNumber);
        }

        public void Add(System.Collections.Generic.IEnumerable<System.ReadOnlyMemory<byte>> nodes)
        {
            Engine.Add(nodes);
        }

        public void StoreMutableLocally(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature)
        {
            // Cast the underlying engine to the concrete type to call the method.
            if (Engine is DhtEngine dhtEngine)
                dhtEngine.StoreMutableLocally(publicKey, salt, value, sequenceNumber, signature);
            // else: Log or handle the case where the underlying engine is not DhtEngine?
            // For now, it will do nothing if the engine doesn't have the method.
        }
    }
}
