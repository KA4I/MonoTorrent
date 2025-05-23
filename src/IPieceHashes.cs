﻿//
// IPieceHashes.cs
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
using System.Diagnostics.CodeAnalysis;

namespace MonoTorrent
{
    public interface IPieceHashes
    {
        int Count { get; }
        bool HasV1Hashes { get; }
        bool HasV2Hashes { get; }

        ReadOnlyPieceHash GetHash (int hashIndex);
        bool IsValid (ReadOnlyPieceHashSpan hashes, int hashIndex);
        bool TryGetV2Hashes (MerkleRoot piecesRoot, [NotNullWhen (true)] out ReadOnlyMerkleTree? merkleTree);
        bool TryGetV2Hashes (MerkleRoot piecesRoot, int layer, int index, int count, int proofCount, Span<byte> hashesAndProofsBuffer, out int bytesWritten);
    }

    public static class PieceHashesExtensions
    {
        public static bool IsValid (this IPieceHashes self, ReadOnlyPieceHash hashes, int hashIndex)
            => self.IsValid (new ReadOnlyPieceHashSpan(hashes.V1Hash.Span, hashes.V2Hash.Span), hashIndex);
    }
}
