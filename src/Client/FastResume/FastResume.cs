//
// FastResume.cs
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
using System.Diagnostics.CodeAnalysis;
using System.IO;

using MonoTorrent.BEncoding;

namespace MonoTorrent.Client
{
    public class FastResume
    {
        // Version 1 stored the Bitfield and Infohash.
        //
        // Version 2 added the UnhashedPieces bitfield.
        //
        static readonly BEncodedNumber FastResumeVersion = 2;

        internal static readonly BEncodedString BitfieldKey = "bitfield";
        internal static readonly BEncodedString BitfieldLengthKey = "bitfield_length";
        internal static readonly BEncodedString InfoHashV1Key = "infohash"; // Keep original key for backwards compat
        internal static readonly BEncodedString InfoHashV2Key = "infohash_v2";
        internal static readonly BEncodedString UnhashedPiecesKey = "unhashed_pieces";
        internal static readonly BEncodedString VersionKey = "version";

        public ReadOnlyBitField Bitfield { get; }

        public InfoHashes InfoHashes { get; }

        public ReadOnlyBitField UnhashedPieces { get; }

        public FastResume (InfoHashes infoHashes, ReadOnlyBitField bitfield, ReadOnlyBitField unhashedPieces)
        {
            InfoHashes = infoHashes ?? throw new ArgumentNullException (nameof (infoHashes));
            Bitfield = new ReadOnlyBitField (bitfield);
            UnhashedPieces = new ReadOnlyBitField (unhashedPieces);

            for (int i = 0; i < Bitfield.Length; i++) {
                if (bitfield[i] && unhashedPieces[i])
                    throw new ArgumentException ($"The bitfield is set to true at index {i} but that piece is marked as unhashed.");
            }
        }

        internal FastResume (BEncodedDictionary dict)
        {
            CheckVersion (dict);
            // V1 hash might not be present in V2-only fastresume
            // V2 hash might not be present in V1-only fastresume
            // At least one must be present.
            if (!dict.ContainsKey (InfoHashV1Key) && !dict.ContainsKey (InfoHashV2Key))
                throw new TorrentException ($"Invalid FastResume data. Neither '{InfoHashV1Key}' nor '{InfoHashV2Key}' were present");

            CheckContent (dict, BitfieldKey);
            CheckContent (dict, BitfieldLengthKey);

            InfoHash? v1 = null;
            InfoHash? v2 = null;

            if (dict.TryGetValue (InfoHashV1Key, out BEncodedValue? v1Val) && v1Val is BEncodedString v1String)
                v1 = InfoHash.FromMemory (v1String.AsMemory ());
            if (dict.TryGetValue (InfoHashV2Key, out BEncodedValue? v2Val) && v2Val is BEncodedString v2String)
                v2 = InfoHash.FromMemory (v2String.AsMemory ());

            if (v1 != null && v1.Span.Length != 20)
                throw new TorrentException ("Invalid FastResume data. V1 infohash was not 20 bytes.");
            if (v2 != null && v2.Span.Length != 32)
                throw new TorrentException ("Invalid FastResume data. V2 infohash was not 32 bytes.");

            InfoHashes = new InfoHashes (v1, v2);

            var data = ((BEncodedString) dict[BitfieldKey]).Span;
            Bitfield = new ReadOnlyBitField (data, (int) ((BEncodedNumber) dict[BitfieldLengthKey]).Number);

            // If we're loading up an older version of the FastResume data then we
            if (dict.ContainsKey (UnhashedPiecesKey)) {
                data = ((BEncodedString) dict[UnhashedPiecesKey]).Span;
                UnhashedPieces = new ReadOnlyBitField (data, Bitfield.Length);
            } else {
                UnhashedPieces = new ReadOnlyBitField (Bitfield.Length);
            }
        }

        static void CheckContent (BEncodedDictionary dict, BEncodedString key)
        {
            if (!dict.ContainsKey (key))
                throw new TorrentException ($"Invalid FastResume data. Key '{key}' was not present");
        }

        static void CheckVersion (BEncodedDictionary dict)
        {
            long? version = (dict[VersionKey] as BEncodedNumber)?.Number;
            if (version.GetValueOrDefault () == 1 || version.GetValueOrDefault () == 2)
                return;

            throw new ArgumentException ($"This FastResume is version {version}, but only version  '1' and '2' are supported");
        }

        public byte[] Encode ()
        {
            var dict = new BEncodedDictionary {
                { VersionKey, FastResumeVersion },
                { BitfieldKey, new BEncodedString(Bitfield.ToBytes()) },
                { BitfieldLengthKey, (BEncodedNumber)Bitfield.Length },
                { UnhashedPiecesKey, new BEncodedString (UnhashedPieces.ToBytes ()) }
            };
            if (InfoHashes.V1 != null)
                dict.Add (InfoHashV1Key, BEncodedString.FromMemory (InfoHashes.V1.AsMemory ()));
            if (InfoHashes.V2 != null)
                dict.Add (InfoHashV2Key, BEncodedString.FromMemory (InfoHashes.V2.AsMemory ()));
            return dict.Encode ();
            
        }

        public void Encode (Stream s)
        {
            byte[] data = Encode ();
            s.Write (data, 0, data.Length);
        }

        public static bool TryLoad (Stream s, [NotNullWhen (true)] out FastResume? fastResume)
        {
            fastResume = Load (s);
            return fastResume != null;
        }

        public static bool TryLoad (string fastResumeFilePath, [NotNullWhen (true)] out FastResume? fastResume)
        {
            fastResume = null;
            try {
                if (File.Exists (fastResumeFilePath)) {
                    using (FileStream s = File.Open (fastResumeFilePath, FileMode.Open)) {
                        fastResume = Load (s);
                    }
                }
            } catch {
            }
            return fastResume != null;
        }

        static FastResume? Load (Stream s)
        {
            try {
                var data = (BEncodedDictionary) BEncodedDictionary.Decode (s);
                return new FastResume (data);
            } catch {
            }
            return null;
        }
    }
}
