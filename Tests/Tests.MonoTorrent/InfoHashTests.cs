//
// BitFieldTest.cs
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
using System.Linq;

using MonoTorrent.BEncoding;
using NUnit.Framework;
using System.Collections.Generic;

namespace MonoTorrent
{
    [TestFixture]
    public class InfoHashTests
    {
        InfoHash Create ()
        {
            return new InfoHash (new byte[] {
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20
            });
        }

        [Test]
        public void HexTest ()
        {
            InfoHash hash = Create ();
            string hex = hash.ToHex ();
            Assert.AreEqual (40, hex.Length, "#1");
            InfoHash other = InfoHash.FromHex (hex);
            Assert.AreEqual (hash, other, "#2");
        }

        [Test]
        public void InvalidArray ()
        {
            Assert.Throws<ArgumentException> (() => new InfoHash (new byte[19]));
            Assert.Throws<ArgumentException> (() => new InfoHash (new byte[21]));
            Assert.Throws<ArgumentException> (() => new InfoHash (new byte[31]));
            Assert.Throws<ArgumentException> (() => new InfoHash (new byte[33]));
        }

        [Test]
        public void InvalidMemory ()
        {
            Assert.Throws<ArgumentException> (() => InfoHash.FromMemory (new byte[19]));
            Assert.Throws<ArgumentException> (() => InfoHash.FromMemory (new byte[21]));
            Assert.Throws<ArgumentException> (() => InfoHash.FromMemory (new byte[31]));
            Assert.Throws<ArgumentException> (() => InfoHash.FromMemory (new byte[33]));
        }

        [Test]
        public void InvalidBase32 ()
        {
            Assert.Throws<ArgumentException> (() => InfoHash.FromBase32 ("YNCKHTQCWBTRNJIV4WNAE52SJUQCZO5"));
            Assert.Throws<ArgumentException> (() => InfoHash.FromBase32 ("YNCKHTQCWBTRNJIV4WNAE52SJUQCZO5?"));
        }

        [Test]
        public void InvalidHex ()
        {
            Assert.Throws<ArgumentException> (() => {
                InfoHash.FromHex ("123123123123123123123");
            });
        }

        [Test]
        public void NullHex ()
        {
            Assert.Throws<ArgumentNullException> (() => {
                InfoHash.FromHex (null);
            });
        }

        [Test]
        public void Equals_False ()
        {
            var first = new InfoHash (Enumerable.Repeat ((byte) 0, 20).ToArray ());
            var second = new InfoHash (Enumerable.Repeat ((byte) 1, 20).ToArray ());
            Assert.IsFalse (first.Equals ((object) second));
            Assert.IsFalse (first.Equals (second));
        }

        [Test]
        public void Equals_True ()
        {
            var first = new InfoHash (Enumerable.Repeat ((byte) 0, 20).ToArray ());
            var second = new InfoHash (Enumerable.Repeat ((byte) 0, 20).ToArray ());
            Assert.IsTrue (first.Equals ((object) second));
            Assert.IsTrue (first.Equals (second));
        }

        [Test]
        public void UrlEncode ()
        {
            var data = new byte[20];
            int index = 0;
            data[index++] = (byte) ' ';
            data[index++] = (byte) '\t';
            data[index++] = (byte) '\r';
            data[index++] = (byte) '&';
            data[index++] = (byte) '+';
            data[index++] = (byte) '?';
            data[index++] = (byte) '#';
            data[index++] = (byte) '%';
            data[index++] = (byte) '+';
            var infoHash = new InfoHash (data);
            var hash = infoHash.UrlEncode ();
            Assert.AreEqual (60, hash.Length);
            Assert.IsFalse (hash.Contains ("+"));
            Assert.IsTrue (hash.Contains ("%20"));
        }

        [Test]
        public void MagnetLink_BEP46_PublicKeyOnly()
        {
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string uri = $"magnet:?xs=urn:btpk:{publicKeyHex}&dn=MyMutableTorrent";
            var magnet = MagnetLink.Parse(uri);

            Assert.IsNull(magnet.InfoHashes, "#1");
            Assert.AreEqual(publicKeyHex, magnet.PublicKeyHex, "#2");
            Assert.IsNull(magnet.SaltHex, "#3");
            Assert.AreEqual("MyMutableTorrent", magnet.Name, "#4");
            Assert.AreEqual(uri, magnet.ToV1String(), "#5"); // ToV1String should handle BEP46 links now
        }

        [Test]
        public void MagnetLink_BEP46_PublicKeyAndSalt()
        {
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string saltHex = "6e";
            string uri = $"magnet:?xs=urn:btpk:{publicKeyHex}&s={saltHex}&dn=MyMutableTorrentWithSalt";
            var magnet = MagnetLink.Parse(uri);

            Assert.IsNull(magnet.InfoHashes, "#1");
            Assert.AreEqual(publicKeyHex, magnet.PublicKeyHex, "#2");
            Assert.AreEqual(saltHex, magnet.SaltHex, "#3");
            Assert.AreEqual("MyMutableTorrentWithSalt", magnet.Name, "#4");
             Assert.AreEqual(uri, magnet.ToV1String(), "#5");
        }

        [Test]
        public void MagnetLink_BEP46_TestVector1()
        {
            // From BEP46
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string uri = $"magnet:?xs=urn:btpk:{publicKeyHex}";
            var magnet = MagnetLink.Parse(uri);

            Assert.IsNull(magnet.InfoHashes, "#1");
            Assert.AreEqual(publicKeyHex, magnet.PublicKeyHex, "#2");
            Assert.IsNull(magnet.SaltHex, "#3");

            // Verify target ID calculation (requires DhtEngine access or duplicating logic)
            var pkBytes = HexDecode(publicKeyHex);
            var targetId = MonoTorrent.Dht.DhtEngine.CalculateMutableTargetId((BEncoding.BEncodedString)pkBytes, null);
             Assert.AreEqual("cc3f9d90b572172053626f9980ce261a850d050b", targetId.ToHex(), "#4 Target ID");
        }

         [Test]
        public void MagnetLink_BEP46_TestVector2()
        {
            // From BEP46
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string saltHex = "6e";
            string uri = $"magnet:?xs=urn:btpk:{publicKeyHex}&s={saltHex}";
            var magnet = MagnetLink.Parse(uri);

            Assert.IsNull(magnet.InfoHashes, "#1");
            Assert.AreEqual(publicKeyHex, magnet.PublicKeyHex, "#2");
            Assert.AreEqual(saltHex, magnet.SaltHex, "#3");

            // Verify target ID calculation
            var pkBytes = HexDecode(publicKeyHex);
            var saltBytes = HexDecode(saltHex);
            var targetId = MonoTorrent.Dht.DhtEngine.CalculateMutableTargetId((BEncoding.BEncodedString)pkBytes, (BEncoding.BEncodedString)saltBytes);
            Assert.AreEqual("59ee7c2cb9b4f7eb1986ee2d18fd2fdb8a56554f", targetId.ToHex(), "#4 Target ID");
        }

        [Test]
        public void MagnetLink_Invalid_Both_xt_xs()
        {
            string infoHashHex = "0101010101010101010101010101010101010101";
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string uri = $"magnet:?xt=urn:btih:{infoHashHex}&xs=urn:btpk:{publicKeyHex}";
            Assert.Throws<FormatException>(() => MagnetLink.Parse(uri));
        }

        [Test]
        public void MagnetLink_Invalid_SaltWithoutPublicKey()
        {
            string infoHashHex = "0101010101010101010101010101010101010101";
            string saltHex = "6e";
            string uri = $"magnet:?xt=urn:btih:{infoHashHex}&s={saltHex}";
             Assert.Throws<FormatException>(() => MagnetLink.Parse(uri));
        }

         [Test]
        public void MagnetLink_Invalid_PublicKeyFormat()
        {
            string publicKeyHex_Short = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63"; // 63 chars
            string publicKeyHex_Long = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e00"; // 65 chars
            string uri_short = $"magnet:?xs=urn:btpk:{publicKeyHex_Short}";
            string uri_long = $"magnet:?xs=urn:btpk:{publicKeyHex_Long}";
            Assert.Throws<FormatException>(() => MagnetLink.Parse(uri_short), "#1 Short");
            Assert.Throws<FormatException>(() => MagnetLink.Parse(uri_long), "#2 Long");
        }

         [Test]
        public void MagnetLink_Invalid_MissingTopic()
        {
            string uri = $"magnet:?dn=MyTorrent";
            Assert.Throws<FormatException>(() => MagnetLink.Parse(uri));
        }

        [Test]
        public void MagnetLink_BEP46_ToUri()
        {
             string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            string saltHex = "6e";
            var magnet = new MagnetLink(publicKeyHex, saltHex, "Test Name", new List<string> { "udp://tracker.example.com:80" });
            string expectedUri = $"magnet:?xs=urn:btpk:{publicKeyHex}&s={saltHex}&dn=Test%20Name&tr=udp%3a%2f%2ftracker.example.com%3a80";
            Assert.AreEqual(expectedUri, magnet.ToV1Uri().OriginalString);
        }

        // Helper needed for tests
        static byte[] HexDecode(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even number of characters");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
