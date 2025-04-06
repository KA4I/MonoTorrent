//
// IDht.cs
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
using System.Collections.Generic;
using System.Text;

using System.Threading.Tasks;
using MonoTorrent.BEncoding;

using MonoTorrent.Dht;

namespace MonoTorrent.Client
{
    public interface IDht
    {
        event EventHandler StateChanged;

        ITransferMonitor Monitor { get; }

        int NodeCount { get; }

        DhtState State { get; }


        TimeSpan AnnounceInterval { get; }

        TimeSpan MinimumAnnounceInterval { get; }


        /// <summary>
        /// Adds DHT nodes to the routing table. This is often used to bootstrap the DHT engine.
        /// </summary>
        /// <param name="nodes">An enumeration of nodes in compact (NodeId+IP+Port) format.</param>
        void Add (IEnumerable<ReadOnlyMemory<byte>> nodes);

        /// <summary>
        /// Puts a mutable item into the DHT.
        /// </summary>
        /// <param name="publicKey">The public key (32 bytes).</param>
        /// <param name="salt">Optional salt.</param>
        /// <param name="value">The value to store.</param>
        /// <param name="sequenceNumber">The sequence number.</param>
        /// <param name="signature">The signature (64 bytes).</param>

        /// <summary>
        /// Gets an item from the DHT.
        /// </summary>
        /// <param name="target">The target ID (infohash for immutable, key hash for mutable).</param>
        /// <param name="sequenceNumber">Optional sequence number for mutable gets.</param>
        /// <returns>A tuple containing the value, public key, signature, and sequence number (if available).</returns>
        Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetAsync(NodeId target, long? sequenceNumber = null);

        /// <param name="cas">Optional Compare-And-Swap sequence number.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task PutMutableAsync (BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null);

        /// <summary>
        /// Explicitly store a mutable item in local DHT storage (for tests or manual replication).
        /// </summary>
        void StoreMutableLocally(BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature);

    }
}
