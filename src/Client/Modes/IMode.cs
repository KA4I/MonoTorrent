﻿//
// IMode.cs
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
using System.Threading;

using MonoTorrent.Messages.Peer;

namespace MonoTorrent.Client.Modes
{
    interface IMode : IDisposable
    {
        bool CanAcceptConnections { get; }
        bool CanHandleMessages { get; }
        bool CanHashCheck { get; }
        TorrentState State { get; }
        CancellationToken Token { get; }
        void HandleFilePriorityChanged (ITorrentManagerFile file, Priority oldPriority);
        /// <summary>
        /// Indicates that we might gain interest in some peers.
        /// </summary>
        List<PeerId> RaiseInterest ();
        void HandleMessage (PeerId id, PeerMessage message, PeerMessage.Releaser releaser);
        void HandlePeerConnected (PeerId id);
        void HandlePeerDisconnected (PeerId id, DisconnectReason reason);
        DisconnectReason ShouldConnect (Peer peer);
        void Tick (int counter);
    }
}
