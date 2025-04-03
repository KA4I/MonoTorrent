//
// TorrentManagerTests.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//   Roo (testing BEP46)
//
// Copyright (C) 2006 Alan McGovern
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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Reflection; // Added for reflection

using MonoTorrent.BEncoding;
using MonoTorrent.Dht;
using MonoTorrent.Dht.Messages; // Required for mocking DhtEngine results

using NUnit.Framework;
using Org.BouncyCastle.Crypto.Parameters; // Replacement for NSec Ed25519
using Org.BouncyCastle.Crypto.Signers;   // Replacement for NSec Ed25519

using Org.BouncyCastle.Security;
namespace MonoTorrent.Client
{
    [TestFixture]
    public class TorrentManagerTests
    {
        // BEP46 Test Data
        const string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
        const string saltHex = "6e";
        static readonly byte[] privateKeyBytes = HexDecode("e06d3183d14159228433ed599221b80bd0a5ce8352e4bdf0262f76786ef1c74d"); // Use only the 32-byte seed
        static readonly byte[] publicKeyBytes = HexDecode(publicKeyHex);
        static readonly byte[] saltBytes = HexDecode(saltHex);
        static readonly InfoHash initialInfoHash = InfoHash.FromHex("0101010101010101010101010101010101010101");
        static readonly InfoHash updatedInfoHash = InfoHash.FromHex("0202020202020202020202020202020202020202");

        ClientEngine engine;
        TestWriter writer;
        TempDir.Releaser tempDir; // Use the actual return type of TempDir.Create()

        [SetUp]
        public void Setup()
        {
            tempDir = TempDir.Create();
            writer = new TestWriter();
            // Use ManualDhtEngine for controlling responses
            var factories = EngineHelpers.Factories.WithDhtCreator(() => new ManualDhtEngine());
            engine = EngineHelpers.Create(EngineHelpers.CreateSettings(cacheDirectory: tempDir.Path, dhtEndPoint: new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0)), factories);
        }

        [TearDown]
        public async Task Teardown()
        {
            await engine.StopAllAsync();
            engine.Dispose();
            tempDir.Dispose(); // No cast needed now
        }

        [Test]
        public void Constructor_MagnetLink_BEP46_NoSalt()
        {
            var link = new MagnetLink(publicKeyHex, null, "Mutable Torrent");
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());

            Assert.IsNotNull(manager.MutablePublicKey, "#1");
            Assert.IsTrue(manager.MutablePublicKey.Span.SequenceEqual(publicKeyBytes), "#2");
            Assert.IsNull(manager.MutableSalt, "#3");
            Assert.IsNull(manager.InfoHashes, "#4 Should be null initially");
            Assert.IsNull(manager.Torrent, "#5 Should be null initially");
            Assert.AreEqual("Mutable Torrent", manager.Name, "#6");
            Assert.IsNull(manager.LastKnownSequenceNumber, "#7");
        }

        [Test]
        public void Constructor_MagnetLink_BEP46_WithSalt()
        {
            var link = new MagnetLink(publicKeyHex, saltHex, "Mutable Torrent Salted");
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());

            Assert.IsNotNull(manager.MutablePublicKey, "#1");
            Assert.IsTrue(manager.MutablePublicKey.Span.SequenceEqual(publicKeyBytes), "#2");
            Assert.IsNotNull(manager.MutableSalt, "#3");
            Assert.IsTrue(manager.MutableSalt.Span.SequenceEqual(saltBytes), "#4");
            Assert.IsNull(manager.InfoHashes, "#5 Should be null initially");
            Assert.IsNull(manager.Torrent, "#6 Should be null initially");
            Assert.AreEqual("Mutable Torrent Salted", manager.Name, "#7");
            Assert.IsNull(manager.LastKnownSequenceNumber, "#8");
        }

        [Test]
        public void Constructor_MagnetLink_Traditional()
        {
            var link = new MagnetLink(initialInfoHash, "Traditional Torrent");
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());

            Assert.IsNull(manager.MutablePublicKey, "#1");
            Assert.IsNull(manager.MutableSalt, "#2");
            Assert.IsNotNull(manager.InfoHashes, "#3");
            Assert.AreEqual(initialInfoHash, manager.InfoHashes.V1OrV2, "#4");
            Assert.IsNull(manager.Torrent, "#5 Should be null initially");
            Assert.AreEqual("Traditional Torrent", manager.Name, "#6");
            Assert.IsNull(manager.LastKnownSequenceNumber, "#7");
        }

        [Test]
        public async Task PerformMutableUpdateCheck_NoUpdate()
        {
            var link = new MagnetLink(publicKeyHex, saltHex);
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());
            var dhtMock = (ManualDhtEngine)engine.DhtEngine;

            bool eventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => eventRaised = true;

            // Simulate DHT returning no value or old sequence number
            dhtMock.GetCallback = (target, seq) => Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((null, null, null, null));

            await manager.PerformMutableUpdateCheckAsync();

            Assert.IsFalse(eventRaised, "#1 Event should not be raised");
            Assert.IsNull(manager.LastKnownSequenceNumber, "#2 Sequence number should not be updated");
        }

        [Test]
        public async Task PerformMutableUpdateCheck_InvalidSignature()
        {
            var link = new MagnetLink(publicKeyHex, saltHex);
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());
            var dhtMock = (ManualDhtEngine)engine.DhtEngine;

            bool eventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => eventRaised = true;

            // Prepare response data
            long newSeq = 1;
            var valueDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var invalidSignature = (BEncodedString)new byte[64]; // All zeros signature

            // Simulate DHT returning a value with an invalid signature
            dhtMock.GetCallback = (target, seq) => Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((
                valueDict,
                (BEncodedString)publicKeyBytes,
                invalidSignature,
                newSeq
            ));

            await manager.PerformMutableUpdateCheckAsync();

            Assert.IsFalse(eventRaised, "#1 Event should not be raised");
            Assert.IsNull(manager.LastKnownSequenceNumber, "#2 Sequence number should not be updated");
        }

        [Test]
        public async Task PerformMutableUpdateCheck_ValidUpdate()
        {
            var link = new MagnetLink(publicKeyHex, saltHex);
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());
            var dhtMock = (ManualDhtEngine)engine.DhtEngine;

            var eventTcs = new TaskCompletionSource<TorrentUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TorrentUpdateAvailable += (s, e) => eventTcs.TrySetResult(e);

            // Prepare response data and sign it
            long newSeq = 1;
            var valueDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var signature = SignMutableData(saltBytes, newSeq, valueDict);

            // Derive the public key from the private seed using BouncyCastle
            var privateKeyParams = new Ed25519PrivateKeyParameters(privateKeyBytes);
            var derivedPublicKeyBytes = privateKeyParams.GeneratePublicKey().GetEncoded();

            // Simulate DHT returning a valid update with the derived public key
             dhtMock.GetCallback = (target, seq) => Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((
                valueDict,
                new BEncodedString(derivedPublicKeyBytes), // Explicitly create BEncodedString
                new BEncodedString(signature),            // Explicitly create BEncodedString
                newSeq
            ));

            await manager.StartAsync(); // Start the manager to allow update check
            await manager.PerformMutableUpdateCheckAsync();

            // Wait for the event handler completion with a timeout (synchronously)
            Assert.IsTrue(eventTcs.Task.Wait(TimeSpan.FromSeconds(5)), "#1 Event should be raised within timeout");
            var receivedArgs = eventTcs.Task.Result;
            Assert.AreEqual(manager, receivedArgs.Manager, "#2 Manager should match");
            // The updated logic calculates the hash from the provided vDict.
            // In this test, vDict only contains {"ih": ...}, so we hash that.
            var expectedInfoDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var expectedCalculatedInfoHash = InfoHash.FromMemory(SHA1.Create().ComputeHash(expectedInfoDict.Encode()));
            Assert.AreEqual(expectedCalculatedInfoHash, receivedArgs.NewInfoHash, "#3 InfoHash should match");
            Assert.AreEqual(newSeq, manager.LastKnownSequenceNumber, "#4 Sequence number should be updated");
            await manager.StopAsync (); // Stop for cleanup
        }

         [Test]
        public async Task PerformMutableUpdateCheck_ValidUpdate_NoSalt()
        {
            var link = new MagnetLink(publicKeyHex, null); // No salt
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());
            var dhtMock = (ManualDhtEngine)engine.DhtEngine;

            var eventTcs = new TaskCompletionSource<TorrentUpdateEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.TorrentUpdateAvailable += (s, e) => eventTcs.TrySetResult(e);

            // Prepare response data and sign it (without salt)
            long newSeq = 5;
            var valueDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var signature = SignMutableData(null, newSeq, valueDict); // Pass null for salt

             // Derive the public key from the private seed using BouncyCastle
            var privateKeyParams = new Ed25519PrivateKeyParameters(privateKeyBytes);
            var derivedPublicKeyBytes = privateKeyParams.GeneratePublicKey().GetEncoded();

            // Simulate DHT returning a valid update with the derived public key
             dhtMock.GetCallback = (target, seq) => Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((
                valueDict,
                 new BEncodedString(derivedPublicKeyBytes), // Explicitly create BEncodedString
                 new BEncodedString(signature),            // Explicitly create BEncodedString
                newSeq
            ));

            await manager.StartAsync(); // Start the manager to allow update check
            await manager.PerformMutableUpdateCheckAsync();

            // Wait for the event handler completion with a timeout (synchronously)
            Assert.IsTrue(eventTcs.Task.Wait(TimeSpan.FromSeconds(5)), "#1 Event should be raised within timeout");
            var receivedArgs = eventTcs.Task.Result;
            Assert.AreEqual(manager, receivedArgs.Manager, "#2 Manager should match");
            // The updated logic calculates the hash from the provided vDict.
            // In this test, vDict only contains {"ih": ...}, so we hash that.
            var expectedInfoDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var expectedCalculatedInfoHash = InfoHash.FromMemory(SHA1.Create().ComputeHash(expectedInfoDict.Encode()));
            Assert.AreEqual(expectedCalculatedInfoHash, receivedArgs.NewInfoHash, "#3 InfoHash should match");
            Assert.AreEqual(newSeq, manager.LastKnownSequenceNumber, "#4 Sequence number should be updated");
            await manager.StopAsync (); // Stop for cleanup
        }

        [Test]
        public async Task PerformMutableUpdateCheck_LowerSequenceNumber()
        {
             var link = new MagnetLink(publicKeyHex, saltHex);
            var manager = new TorrentManager(engine, link, "savepath", new TorrentSettings());
            var dhtMock = (ManualDhtEngine)engine.DhtEngine;

            TorrentUpdateEventArgs? receivedArgs = null;
            manager.TorrentUpdateAvailable += (s, e) => receivedArgs = e;

            // Set an initial sequence number
            manager.GetType().GetProperty("LastKnownSequenceNumber", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, 10L);


            // Prepare response data with a lower sequence number
            long lowerSeq = 5;
            var valueDict = new BEncodedDictionary { { "ih", (BEncodedString)updatedInfoHash.Span.ToArray() } };
            var signature = SignMutableData(saltBytes, lowerSeq, valueDict);

            // Simulate DHT returning an older update
             dhtMock.GetCallback = (target, seq) => Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((
                valueDict,
                (BEncodedString)publicKeyBytes,
                (BEncodedString)signature,
                lowerSeq
            ));

            await manager.PerformMutableUpdateCheckAsync();

            Assert.IsNull(receivedArgs, "#1 Event should not be raised");
            Assert.AreEqual(10L, manager.LastKnownSequenceNumber, "#2 Sequence number should not change");
        }

        // Helper to sign mutable data for tests
        private byte[] SignMutableData(byte[]? salt, long sequenceNumber, BEncodedValue value)
        {
            // Construct the data to sign
            int valueLength = value.LengthInBytes();
            int seqLength = new BEncodedString("seq").LengthInBytes() + new BEncodedNumber(sequenceNumber).LengthInBytes();

            // Correctly BEncode the salt if it exists
            BEncodedString? bencodedSalt = salt == null ? null : new BEncodedString(salt);
            int saltLength = (bencodedSalt == null || bencodedSalt.Span.Length == 0) ? 0 : new BEncodedString("salt").LengthInBytes() + bencodedSalt.LengthInBytes();
            int totalLength = saltLength + seqLength + new BEncodedString("v").LengthInBytes() + valueLength;

            using var rented = MemoryPool.Default.Rent(totalLength, out Memory<byte> dataToSignMem);
            Span<byte> dataToSign = dataToSignMem.Span.Slice(0, totalLength);
            int offset = 0;

            if (saltLength > 0)
            {
                offset += new BEncodedString("salt").Encode(dataToSign.Slice(offset));
                offset += bencodedSalt!.Encode(dataToSign.Slice(offset)); // salt is checked for null by saltLength > 0
            }
            offset += new BEncodedString("seq").Encode(dataToSign.Slice(offset));
            offset += new BEncodedNumber(sequenceNumber).Encode(dataToSign.Slice(offset));
            offset += new BEncodedString("v").Encode(dataToSign.Slice(offset));
            offset += value.Encode(dataToSign.Slice(offset));

            // Sign using the test private key with BouncyCastle
            var signer = new Ed25519Signer();
            var privateKeyParams = new Ed25519PrivateKeyParameters(privateKeyBytes);
            signer.Init(true, privateKeyParams); // true for signing
            signer.BlockUpdate(dataToSign.ToArray(), 0, dataToSign.Length); // Use the actual data span
            return signer.GenerateSignature();
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



        // Helper function to sign mutable data (similar to ClientEngineTests version)
        static BEncodedString SignMutableDataHelper(Ed25519PrivateKeyParameters privateKey, BEncodedString? salt, long sequenceNumber, BEncodedValue value)
        {
            // Construct the data to sign: "salt" + salt + "seq" + seq + "v" + value
            int saltKeyLength = new BEncodedString("salt").LengthInBytes();
            int seqKeyLength = new BEncodedString("seq").LengthInBytes();
            int vKeyLength = new BEncodedString("v").LengthInBytes();

            int saltLength = (salt == null || salt.Span.Length == 0) ? 0 : (saltKeyLength + salt.LengthInBytes());
            int seqLength = seqKeyLength + new BEncodedNumber(sequenceNumber).LengthInBytes();
            int valueLength = vKeyLength + value.LengthInBytes();
            int totalLength = saltLength + seqLength + valueLength;

            using var rented = System.Buffers.MemoryPool<byte>.Shared.Rent(totalLength);
            Span<byte> dataToSign = rented.Memory.Span.Slice(0, totalLength);

            int offset = 0;
            if (saltLength > 0)
            {
                offset += new BEncodedString("salt").Encode(dataToSign.Slice(offset));
                offset += salt!.Encode(dataToSign.Slice(offset));
            }
            offset += new BEncodedString("seq").Encode(dataToSign.Slice(offset));
            offset += new BEncodedNumber(sequenceNumber).Encode(dataToSign.Slice(offset));
            offset += new BEncodedString("v").Encode(dataToSign.Slice(offset));
            offset += value.Encode(dataToSign.Slice(offset));

            // Sign the data
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey); // true for signing
            signer.BlockUpdate(dataToSign.ToArray(), 0, dataToSign.Length); // Use the byte array overload
            byte[] signatureBytes = signer.GenerateSignature();

            return new BEncodedString(signatureBytes);
        }

        [Test]
        public void VerifyMutableSignature_ValidAndInvalid()
        {
            // 1. Generate Keys
            var random = new SecureRandom();
            var keyPairGenerator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
            keyPairGenerator.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(random, 256));
            var keyPair = keyPairGenerator.GenerateKeyPair();
            var privateKeyParams = (Ed25519PrivateKeyParameters)keyPair.Private;
            var publicKeyParams = (Ed25519PublicKeyParameters)keyPair.Public;
            var publicKeyBytesBEncoded = new BEncodedString(publicKeyParams.GetEncoded()); // BEncoded public key

            // 2. Prepare Test Data
            var saltBEncoded = new BEncodedString("test-salt");
            long sequenceNumber = 123;
            var value = new BEncodedDictionary { { "info", new BEncodedString("test-data") } };

            // 3. Sign Data (Create Valid Signature)
            var validSignature = SignMutableDataHelper(privateKeyParams, saltBEncoded, sequenceNumber, value);

            // 4. Create a TorrentManager instance (needed to call the method)
            // Use the engine from Setup
            var dummyMagnet = new MagnetLink(InfoHash.FromMemory(new byte[20]), "dummy");
            var managerInstance = new TorrentManager(engine, dummyMagnet, "dummy_path", new TorrentSettings());

            // 5. Get the private method via reflection
            var verifyMethod = typeof(TorrentManager).GetMethod("VerifyMutableSignature", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(verifyMethod, "Failed to get VerifyMutableSignature method via reflection");

            // 6. Test with VALID signature
            object[] parametersValid = { publicKeyBytesBEncoded, saltBEncoded, sequenceNumber, value, validSignature };
            bool isValid = (bool)verifyMethod.Invoke(managerInstance, parametersValid);
            Assert.IsTrue(isValid, "Verification should succeed with a valid signature.");

            // 7. Test with INVALID signature (tamper with the valid one)
            var invalidSignatureBytes = validSignature.Span.ToArray();
            invalidSignatureBytes[0] ^= 0xFF; // Flip the first byte
            var invalidSignature = new BEncodedString(invalidSignatureBytes);
            object[] parametersInvalid = { publicKeyBytesBEncoded, saltBEncoded, sequenceNumber, value, invalidSignature };
            bool isInvalid = (bool)verifyMethod.Invoke(managerInstance, parametersInvalid);
            Assert.IsFalse(isInvalid, "Verification should fail with an invalid signature.");

            // 8. Test with null salt (should still work if signing was done correctly with null salt)
            long seqNullSalt = 456;
            var valueNullSalt = new BEncodedDictionary { { "data", new BEncodedNumber(789) } };
            var validSigNullSalt = SignMutableDataHelper(privateKeyParams, null, seqNullSalt, valueNullSalt);
            object[] parametersNullSalt = { publicKeyBytesBEncoded, null, seqNullSalt, valueNullSalt, validSigNullSalt };
            bool isValidNullSalt = (bool)verifyMethod.Invoke(managerInstance, parametersNullSalt);
            Assert.IsTrue(isValidNullSalt, "Verification should succeed with null salt and valid signature.");

             // 9. Test with incorrect public key
            var otherKeyPairGenerator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
            otherKeyPairGenerator.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(random, 256));
            var otherKeyPair = otherKeyPairGenerator.GenerateKeyPair();
            var otherPublicKeyParams = (Ed25519PublicKeyParameters)otherKeyPair.Public;
            var wrongPublicKeyBytesBEncoded = new BEncodedString(otherPublicKeyParams.GetEncoded());
            object[] parametersWrongKey = { wrongPublicKeyBytesBEncoded, saltBEncoded, sequenceNumber, value, validSignature };
            bool isWrongKey = (bool)verifyMethod.Invoke(managerInstance, parametersWrongKey);
            Assert.IsFalse(isWrongKey, "Verification should fail with the wrong public key.");
        }
    }
}
