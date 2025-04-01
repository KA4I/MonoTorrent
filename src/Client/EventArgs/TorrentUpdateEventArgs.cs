//
// TorrentUpdateEventArgs.cs
//
// Authors:
//   Roo (implementing BEP46)
//
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

namespace MonoTorrent.Client
{
    /// <summary>
    /// Provides data for the <see cref="TorrentManager.TorrentUpdateAvailable"/> event.
    /// </summary>
    public class TorrentUpdateEventArgs : EventArgs
    {
        /// <summary>
        /// The TorrentManager instance this event relates to.
        /// </summary>
        public TorrentManager Manager { get; }

        /// <summary>
        /// The new InfoHash received from the DHT update (BEP46).
        /// </summary>
        public InfoHash NewInfoHash { get; }

        /// <summary>
        /// Initializes a new instance of the TorrentUpdateEventArgs class.
        /// </summary>
        /// <param name="manager">The TorrentManager instance.</param>
        /// <param name="newInfoHash">The new InfoHash.</param>
        public TorrentUpdateEventArgs(TorrentManager manager, InfoHash newInfoHash)
        {
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));
            NewInfoHash = newInfoHash ?? throw new ArgumentNullException(nameof(newInfoHash));
        }
    }
}