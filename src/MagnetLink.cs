//
// MagnetLink.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
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
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MonoTorrent
{
    public class MagnetLink
    {
        /// <summary>
        /// The list of tracker Urls.
        /// </summary>
        public IList<string> AnnounceUrls { get; }

        /// <summary>
        /// The infohashes for this torrent.
        /// </summary>
        public InfoHashes? InfoHashes { get; private set; }

        /// <summary>
        /// The size in bytes of the data, if available.
        /// </summary>
        public long? Size {
            get;
        }

        /// <summary>
        /// The display name of the torrent, if available.
        /// </summary>
        public string? Name {
            get;
        }

        /// <summary>
        /// The list of webseed Urls.
        /// </summary>
        public IList<string> Webseeds {
            get;
        }

        /// <summary>
        /// The public key for mutable torrent updates (BEP46). Hex encoded.
        /// </summary>
        public string? PublicKeyHex { get; }

        /// <summary>
        /// The optional salt for mutable torrent updates (BEP46). Hex encoded.
        /// </summary>
        public string? SaltHex { get; }

        public MagnetLink (InfoHash infoHash, string? name = null, IList<string>? announceUrls = null, IEnumerable<string>? webSeeds = null, long? size = null)
            : this (InfoHashes.FromInfoHash (infoHash), name, announceUrls, webSeeds, size)
        {

        }

        // Constructor for traditional InfoHash based links (xt=)
        public MagnetLink (InfoHashes infoHashes, string? name = null, IList<string>? announceUrls = null, IEnumerable<string>? webSeeds = null, long? size = null)
        {
            InfoHashes = infoHashes ?? throw new ArgumentNullException (nameof (infoHashes));
            Name = name;
            AnnounceUrls = new List<string> (announceUrls ?? Array.Empty<string> ()).AsReadOnly ();
            Webseeds = new List<string> (webSeeds ?? Array.Empty<string> ()).AsReadOnly ();
            Size = size;
        }

        // Constructor for BEP46 mutable links (xs=)
        public MagnetLink (string publicKeyHex, string? saltHex = null, string? name = null, IList<string>? announceUrls = null, IEnumerable<string>? webSeeds = null)
        {
             if (string.IsNullOrEmpty(publicKeyHex) || publicKeyHex.Length != 64) // ed25519 public key is 32 bytes = 64 hex chars
                throw new ArgumentException ("Public key must be a 64 character hex string.", nameof(publicKeyHex));
             if (saltHex != null && saltHex.Length == 0) // Allow null, but not empty string for salt
                 saltHex = null;
             if (!IsValidHex(saltHex))
                 throw new ArgumentException("Salt must be a valid hex string.", nameof(saltHex));
            PublicKeyHex = publicKeyHex;
            SaltHex = saltHex;
            Name = name;
            AnnounceUrls = new List<string> (announceUrls ?? Array.Empty<string> ()).AsReadOnly ();
            Webseeds = new List<string> (webSeeds ?? Array.Empty<string> ()).AsReadOnly ();
            // Size is not typically part of BEP46 magnet links
        }

        // Private constructor used by FromUri
        private MagnetLink(InfoHashes? infoHashes, string? publicKeyHex, string? saltHex, string? name, IList<string> announceUrls, IList<string> webSeeds, long? size)
        {
            InfoHashes = infoHashes;
            PublicKeyHex = publicKeyHex;
            SaltHex = saltHex;
            Name = name;
            AnnounceUrls = new List<string>(announceUrls).AsReadOnly();
            Webseeds = new List<string>(webSeeds).AsReadOnly();
            Size = size;
        }

        /// <summary>
        /// Parses a magnet link from the given string. The uri should be in the form magnet:?xt=urn:btih:
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        public static MagnetLink Parse (string uri)
        {
            return FromUri (new Uri (uri));
        }

        /// <summary>
        /// Returns <see langword="true"/> if a bitorrent magnet link was successfully parsed from the given string. Otherwise
        /// return false.
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="magnetLink"></param>
        /// <returns></returns>
        public static bool TryParse (string uri, [NotNullWhen(true)] out MagnetLink? magnetLink)
        {
            try {
                magnetLink = Parse (uri);
            } catch {
                magnetLink = null;
            }
            return magnetLink != null;
        }

        /// <summary>
        /// Parses a magnet link from the given Uri. The uri should be in the form magnet:?xt=urn:btih:
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        public static MagnetLink FromUri (Uri uri)
        {
            InfoHashes? parsedInfoHashes = null;
            string? parsedPublicKeyHex = null;
            string? parsedSaltHex = null;
            string? parsedName = null;
            var parsedAnnounceUrls = new List<string> ();
            var parsedWebSeeds = new List<string> ();
            long? parsedSize = null;
            bool hasExtendedParameters = false;

            if (uri.Scheme != "magnet")
                throw new FormatException ("Magnet links must start with 'magnet:'.");

            string[] parameters = uri.Query.Substring (1).Split ('&');
            for (int i = 0; i < parameters.Length; i++) {
                string[] keyval = parameters[i].Split ('=');
                if (keyval.Length != 2) {
                    // Skip anything we don't understand. Urls could theoretically contain many
                    // unknown parameters.
                    continue;
                }
                string key = keyval[0];
                string value = keyval[1];

                // Check for keys with dots in them which could be extended formats
                string baseKey = key.Contains(".") ? key.Substring(0, key.IndexOf('.')) : key;

                switch (baseKey) {
                    case "xt": // Exact Topic (InfoHash)
                        if (parsedPublicKeyHex != null)
                             throw new FormatException("Magnet link cannot contain both 'xt' (infohash) and 'xs' (public key).");
                        
                        // Skip if not a standard hash format 
                        if (!value.StartsWith("urn:btih:") && !value.StartsWith("urn:sha1:") && !value.StartsWith("urn:btmh:")) {
                            hasExtendedParameters = true;
                            continue;
                        }

                        string hashValue = value.Substring(9);
                        if (value.StartsWith("urn:btih:") || value.StartsWith("urn:sha1:"))
                        {
                             if (parsedInfoHashes?.V1 != null)
                                throw new FormatException ("More than one v1 infohash ('xt=urn:btih:' or 'xt=urn:sha1:') in magnet link is not allowed.");

                            InfoHash v1Hash;
                            if (hashValue.Length == 32)
                                v1Hash = InfoHash.FromBase32(hashValue);
                            else if (hashValue.Length == 40)
                                v1Hash = InfoHash.FromHex(hashValue);
                            else
                                throw new FormatException("Infohash ('xt=urn:btih:' or 'xt=urn:sha1:') must be 32 char base32 or 40 char hex encoded.");
                            parsedInfoHashes = new InfoHashes(v1Hash, parsedInfoHashes?.V2);
                        }
                        else // urn:btmh:
                        {
                            if (parsedInfoHashes?.V2 != null)
                                throw new FormatException ("More than one v2 multihash ('xt=urn:btmh:') in magnet link is not allowed.");

                            parsedInfoHashes = new InfoHashes(parsedInfoHashes?.V1, InfoHash.FromMultiHash(hashValue));
                        }
                        break;

                    case "xs": // Exact Source (BEP46 Public Key)
                        if (parsedInfoHashes != null)
                            throw new FormatException("Magnet link cannot contain both 'xt' (infohash) and 'xs' (public key).");
                        if (parsedPublicKeyHex != null)
                            throw new FormatException("More than one public key ('xs=') in magnet link is not allowed.");
                        if (!value.StartsWith("urn:btpk:"))
                            throw new FormatException("Exact source ('xs=') must start with 'urn:btpk:'.");

                        string pkHex = value.Substring(9);
                        if (pkHex.Length != 64) // ed25519 pubkey is 32 bytes = 64 hex chars
                            throw new FormatException("Public key ('xs=') must be a 64 character hex string.");
                        if (!IsValidHex(pkHex))
                             throw new FormatException("Public key ('xs=') must be a valid hex string.");
                        parsedPublicKeyHex = pkHex;
                        break;

                    case "s": // Salt (BEP46)
                        if (!IsValidHex(value))
                            throw new FormatException("Salt ('s=') must be a valid hex string.");
                        parsedSaltHex = value;
                        break;

                    case "tr": // Tracker address
                        parsedAnnounceUrls.Add (value.UrlDecodeUTF8 ());
                        break;

                    case "as": // Acceptable Source (WebSeed)
                        parsedWebSeeds.Add (value.UrlDecodeUTF8 ());
                        break;

                    case "dn": // Display Name
                        parsedName = value.UrlDecodeUTF8 ();
                        break;

                    case "xl": // Exact Length
                        parsedSize = long.Parse (value);
                        break;

                    //case "kt": // Keyword topic
                    //case "mt": // Manifest topic
                    // Unused Parameters:
                    default:
                        // Unknown/unsupported parameters are ignored
                        break;
                }
            }

            // Only require a valid infohash or public key if we haven't seen any extended parameters
            if (!hasExtendedParameters && parsedInfoHashes == null && parsedPublicKeyHex == null)
                throw new FormatException ("The magnet link must contain either an 'xt' (infohash) or 'xs' (public key) parameter.");

            // Salt ('s=') is only valid with public key ('xs=')
            if (parsedSaltHex != null && parsedPublicKeyHex == null)
                 throw new FormatException("The salt parameter ('s=') is only valid when a public key ('xs=') is provided.");

            return new MagnetLink (parsedInfoHashes, parsedPublicKeyHex, parsedSaltHex, parsedName, parsedAnnounceUrls, parsedWebSeeds, parsedSize);
        }

        public string ToV1String ()
        {
            return ConvertToString ();
        }

        public Uri ToV1Uri ()
        {
            return new Uri (ToV1String ());
        }

        string ConvertToString ()
        {
            var sb = new StringBuilder ();
            sb.Append ("magnet:?");

            if (InfoHashes != null) {
                // Output hashes based on protocol
                if (InfoHashes.Protocol == TorrentProtocol.V1 || InfoHashes.Protocol == TorrentProtocol.Hybrid) {
                    sb.Append ("xt=urn:btih:");
                    sb.Append (InfoHashes.V1!.ToHex ()); // V1 must be non-null here
                }
                if (InfoHashes.Protocol == TorrentProtocol.V2 || InfoHashes.Protocol == TorrentProtocol.Hybrid) {
                    if (sb[sb.Length - 1] != '?') // Add '&' if not the first parameter
                        sb.Append ('&');
                    sb.Append ("xt=urn:btmh:");
                    // BEP52 specifies the multihash format for V2: 0x12 (sha2-256), 0x20 (32 bytes length), followed by the 32-byte hash
                    // Then this entire sequence is Base32 encoded. However, common practice and examples
                    // seem to just Base32 encode the raw 32-byte SHA256 hash directly for the urn:btmh value.
                    // Let's follow the common practice for now.
                    sb.Append (InfoHashes.V2!.ToBase32 ()); // V2 must be non-null here
                }
            } else if (PublicKeyHex != null) {
                 sb.Append ("xs=urn:btpk:");
                 sb.Append (PublicKeyHex);
                 if (SaltHex != null) {
                     sb.Append ("&s=");
                     // Salt does not need special encoding? BEP just shows hex.
                     sb.Append (SaltHex);
                 }
            } else {
                // Should not happen due to constructor validation
                throw new InvalidOperationException("MagnetLink has neither InfoHash nor PublicKey.");
            }


            if (Name is { Length: > 0 }) {
                sb.Append ("&dn=");
                sb.Append (Name.UrlEncodeQueryUTF8 ());
            }

            foreach (string tracker in AnnounceUrls) {
                sb.Append ("&tr=");
                sb.Append (tracker.UrlEncodeQueryUTF8 ());
            }

            foreach (string webseed in Webseeds) {
                sb.Append ("&as=");
                sb.Append (webseed.UrlEncodeQueryUTF8 ());
            }

             if (Size.HasValue) {
                sb.Append ("&xl=");
                sb.Append (Size.Value);
            }


            return sb.ToString ();
        }

        // Helper method to validate hex strings
        private static bool IsValidHex(string? hex)
        {
            if (hex == null) return true; // Null is allowed for salt
            if (hex.Length % 2 != 0) return false; // Must have even length

            foreach (char c in hex)
            {
                bool isHexDigit = (c >= '0' && c <= '9') ||
                                  (c >= 'a' && c <= 'f') ||
                                  (c >= 'A' && c <= 'F');
                if (!isHexDigit) return false;
            }
            return true;
        }
    }
}
