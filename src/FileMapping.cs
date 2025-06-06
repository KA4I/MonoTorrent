//
// FileMapping.cs
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


using System;

namespace MonoTorrent
{
    public struct FileMapping
    {
        /// <summary>
        /// This is the full path to the file on disk
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// This is the relative path to the file within the Torrent
        /// </summary>
        public TorrentPath Destination { get; }

        /// <summary>
        /// The length of the file
        /// </summary>
        public long Length { get; }

        public FileMapping (string source, TorrentPath destination, long length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException (nameof(length), "Length must be zero or greater");
            Source = source ?? throw new ArgumentNullException (nameof(source));
            Destination = destination;
            Length = length;
        }
    }
}
