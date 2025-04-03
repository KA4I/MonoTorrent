﻿//
// ClientEngineTests.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent.BEncoding; // Added for BEncodedString
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using Org.BouncyCastle.Crypto.Parameters; // Added for Ed25519 keys
using Org.BouncyCastle.Crypto.Signers;   // Added for Ed25519 signing
using Org.BouncyCastle.Security;
using MonoTorrent.Dht; // Added for ManualDhtEngine
using MonoTorrent.PieceWriter;

using NUnit.Framework;

namespace MonoTorrent.Client
{

    [TestFixture]
    public class ClientEngineTests
    {
        [Test]
        public async Task AddPeers_Dht ()
        {
            var dht = new ManualDhtEngine ();
            var factories = EngineHelpers.Factories.WithDhtCreator (() => dht);
            var settings = EngineHelpers.CreateSettings (dhtEndPoint: new IPEndPoint (IPAddress.Any, 1234));

            using var engine = new ClientEngine (settings, factories);
            var manager = await engine.AddAsync (new MagnetLink (InfoHash.FromMemory (new byte[20])), "asd");

            var tcs = new TaskCompletionSource<DhtPeersAdded> ();
            manager.PeersFound += (o, e) => {
                if (e is DhtPeersAdded args)
                    tcs.TrySetResult (args);
            };

            var peer = new PeerInfo (new Uri ("ipv4://123.123.123.123:1515"));
            dht.RaisePeersFound (manager.InfoHashes.V1OrV2, new[] { peer });
            var result = await tcs.Task.WithTimeout (TimeSpan.FromSeconds (5));
            Assert.AreEqual (1, result.NewPeers, "#2");
            Assert.AreEqual (0, result.ExistingPeers, "#3");
            Assert.AreEqual (1, manager.Peers.AvailablePeers.Count, "#4");
        }

        [Test]
        public async Task AddPeers_Dht_Private ()
        {
            // You can't manually add peers to private torrents
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            var editor = new TorrentEditor (rig.TorrentDict) {
                CanEditSecureMetadata = true,
                Private = true
            };

            var manager = await rig.Engine.AddAsync (editor.ToTorrent (), "path", new TorrentSettings ());

            var dht = (ManualDhtEngine) rig.Engine.DhtEngine;

            var tcs = new TaskCompletionSource<DhtPeersAdded> ();
            manager.PeersFound += (o, e) => {
                if (e is DhtPeersAdded args)
                    tcs.TrySetResult (args);
            };

            var peer = rig.CreatePeer (false).Peer;
            dht.RaisePeersFound (manager.InfoHashes.V1OrV2, new[] { peer.Info });
            var result = await tcs.Task.WithTimeout (TimeSpan.FromSeconds (5));
            Assert.AreEqual (0, result.NewPeers, "#2");
            Assert.AreEqual (0, result.ExistingPeers, "#3");
            Assert.AreEqual (0, manager.Peers.AvailablePeers.Count, "#4");
        }

        [Test]
        public async Task AddPeers_LocalPeerDiscovery ()
        {
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            var localPeer = (ManualLocalPeerListener) rig.Engine.LocalPeerDiscovery;

            var tcs = new TaskCompletionSource<LocalPeersAdded> ();
            var manager = rig.Engine.Torrents[0];
            manager.PeersFound += (o, e) => {
                if (e is LocalPeersAdded args)
                    tcs.TrySetResult (args);
            };

            localPeer.RaisePeerFound (manager.InfoHashes.V1OrV2, rig.CreatePeer (false).Uri);
            var result = await tcs.Task.WithTimeout (TimeSpan.FromSeconds (5));
            Assert.AreEqual (1, result.NewPeers, "#2");
            Assert.AreEqual (0, result.ExistingPeers, "#3");
            Assert.AreEqual (1, manager.Peers.AvailablePeers.Count, "#4");
        }

        [Test]
        public async Task AddPeers_LocalPeerDiscovery_Private ()
        {
            // You can't manually add peers to private torrents
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            var editor = new TorrentEditor (rig.TorrentDict) {
                CanEditSecureMetadata = true,
                Private = true
            };

            var manager = await rig.Engine.AddAsync (editor.ToTorrent (), "path", new TorrentSettings ());

            var localPeer = (ManualLocalPeerListener) rig.Engine.LocalPeerDiscovery;

            var tcs = new TaskCompletionSource<LocalPeersAdded> ();
            manager.PeersFound += (o, e) => {
                if (e is LocalPeersAdded args)
                    tcs.TrySetResult (args);
            };

            localPeer.RaisePeerFound (manager.InfoHashes.V1OrV2, rig.CreatePeer (false).Uri);
            var result = await tcs.Task.WithTimeout (TimeSpan.FromSeconds (5));
            Assert.AreEqual (0, result.NewPeers, "#2");
            Assert.AreEqual (0, result.ExistingPeers, "#3");
            Assert.AreEqual (0, manager.Peers.AvailablePeers.Count, "#4");
        }

        [Test]
        public void CacheDirectory_IsFile_Constructor ()
        {
            var tmp = TempDir.Create ();
            var cachePath = Path.Combine (tmp.Path, "test.file");
            using (var file = File.Create (cachePath)) { }
            Assert.Throws<ArgumentException> (() => new ClientEngine (new EngineSettingsBuilder { CacheDirectory = cachePath }.ToSettings ()));
        }

        [Test]
        public void CacheDirectory_IsFile_UpdateSettings ()
        {
            var engine = EngineHelpers.Create ();
            var tmp = TempDir.Create ();
            var cachePath = Path.Combine (tmp.Path, "test.file");
            using (var file = File.Create (cachePath)) { }
            Assert.ThrowsAsync<ArgumentException> (() => engine.UpdateSettingsAsync (new EngineSettingsBuilder { CacheDirectory = cachePath }.ToSettings ()));
        }

        [Test]
        public async Task ContainingDirectory_InvalidCharacters ()
        {
            // You can't manually add peers to private torrents
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            await rig.Engine.RemoveAsync (rig.Engine.Torrents[0]);

            var editor = new TorrentEditor (rig.TorrentDict);
            editor.CanEditSecureMetadata = true;
            editor.Name = $"{Path.GetInvalidPathChars()[0]}test{Path.GetInvalidPathChars ()[0]}";

            var manager = await rig.Engine.AddAsync (editor.ToTorrent (), "path", new TorrentSettings ());
            Assert.IsFalse(manager.ContainingDirectory.Contains (manager.Torrent.Name));
            Assert.IsTrue (manager.ContainingDirectory.StartsWith (manager.SavePath));
            Assert.AreEqual (Path.GetFullPath (manager.ContainingDirectory), manager.ContainingDirectory);
            Assert.AreEqual (Path.GetFullPath (manager.SavePath), manager.SavePath);
        }

        [Test]
        public async Task ContainingDirectory_PathBusting ()
        {
            // You can't manually add peers to private torrents
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            await rig.Engine.RemoveAsync (rig.Engine.Torrents[0]);

            var editor = new TorrentEditor (rig.TorrentDict);
            editor.CanEditSecureMetadata = true;
            editor.Name = $"..{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}test{Path.GetInvalidPathChars ()[0]}";

            Assert.ThrowsAsync<ArgumentException> (() => rig.Engine.AddAsync (editor.ToTorrent (), "path", new TorrentSettings ()));
        }

        [Test]
        [TestCase (true)]
        [TestCase (false)]
        public async Task UsePartialFiles_InitiallyOff_ChangeFullPath_ToggleOn (bool createFile)
        {
            var writer = new TestWriter ();
            var pieceLength = Constants.BlockSize * 4;
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (usePartialFiles: false), EngineHelpers.Factories.WithPieceWriterCreator (t => writer));
            var torrent = TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary _);

            using var tempDir = TempDir.Create ();
            var manager = await engine.AddAsync (torrent, tempDir.Path);
            Assert.AreEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);

            var newPath = Path.GetFullPath (Path.Combine (tempDir.Path, "new_full_path.fake"));

            await manager.MoveFileAsync (manager.Files[0], newPath);
            Assert.AreEqual (newPath, manager.Files[0].FullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadCompleteFullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadIncompleteFullPath);

            if (createFile)
                await writer.CreateAsync (manager.Files[0], FileCreationOptions.PreferSparse);

            var settings = new EngineSettingsBuilder (engine.Settings) { UsePartialFiles = true }.ToSettings ();
            await engine.UpdateSettingsAsync (settings);
            Assert.AreEqual (newPath + TorrentFileInfo.IncompleteFileSuffix, manager.Files[0].FullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadCompleteFullPath);
            Assert.AreEqual (newPath + TorrentFileInfo.IncompleteFileSuffix, manager.Files[0].DownloadIncompleteFullPath);

            if (createFile) {
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath));
                Assert.IsTrue (writer.FilesWithLength.ContainsKey (newPath + TorrentFileInfo.IncompleteFileSuffix));
            } else {
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath));
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath + TorrentFileInfo.IncompleteFileSuffix));
            }
        }

        [Test]
        public async Task UsePartialFiles_InitiallyOff_ToggleOn ()
        {
            var pieceLength = Constants.BlockSize * 4;
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (usePartialFiles: false));
            var torrent = TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary _);

            var manager = await engine.AddAsync (torrent, "");
            Assert.AreEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);

            var settings = new EngineSettingsBuilder (engine.Settings) { UsePartialFiles = true }.ToSettings ();
            await engine.UpdateSettingsAsync (settings);
            Assert.AreNotEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);
        }

        [Test]
        public async Task UsePartialFiles_InitiallyOn_ToggleOff ()
        {
            var pieceLength = Constants.BlockSize * 4;
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (usePartialFiles: true));
            var torrent = TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary _);

            var manager = await engine.AddAsync (torrent, "");
            Assert.AreNotEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);

            var settings = new EngineSettingsBuilder (engine.Settings) { UsePartialFiles = false }.ToSettings ();
            await engine.UpdateSettingsAsync (settings);
            Assert.AreEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);
        }

        [Test]
        [TestCase (true)]
        [TestCase (false)]
        public async Task UsePartialFiles_InitiallyOn_ChangeFullPath_ToggleOff (bool createFile)
        {
            var writer = new TestWriter ();
            var pieceLength = Constants.BlockSize * 4;
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (usePartialFiles: true), EngineHelpers.Factories.WithPieceWriterCreator (t => writer));
            var torrent = TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary _);

            using var tempDir = TempDir.Create ();
            var manager = await engine.AddAsync (torrent, tempDir.Path);
            Assert.AreNotEqual (manager.Files[0].DownloadCompleteFullPath, manager.Files[0].DownloadIncompleteFullPath);

            var newPath = Path.GetFullPath (Path.Combine (tempDir.Path, "new_full_path.fake"));

            await manager.MoveFileAsync (manager.Files[0], newPath);
            Assert.AreEqual (newPath + TorrentFileInfo.IncompleteFileSuffix, manager.Files[0].FullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadCompleteFullPath);
            Assert.AreEqual (newPath + TorrentFileInfo.IncompleteFileSuffix, manager.Files[0].DownloadIncompleteFullPath);

            if (createFile)
                await writer.CreateAsync (manager.Files[0], FileCreationOptions.PreferSparse);

            var settings = new EngineSettingsBuilder (engine.Settings) { UsePartialFiles = false }.ToSettings ();
            await engine.UpdateSettingsAsync (settings);
            Assert.AreEqual (newPath, manager.Files[0].FullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadCompleteFullPath);
            Assert.AreEqual (newPath, manager.Files[0].DownloadIncompleteFullPath);

            if (createFile) {
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath + TorrentFileInfo.IncompleteFileSuffix));
                Assert.IsTrue (writer.FilesWithLength.ContainsKey (newPath));
            } else {
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath));
                Assert.IsFalse (writer.FilesWithLength.ContainsKey (newPath + TorrentFileInfo.IncompleteFileSuffix));
            }
        }

        [Test]
        public void DownloadMetadata_Cancelled ()
        {
            var cts = new CancellationTokenSource ();
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings ());
            var task = engine.DownloadMetadataAsync (new MagnetLink (InfoHashes.FromV1 (new InfoHash (new byte[20]))), cts.Token);
            cts.Cancel ();
            Assert.ThrowsAsync<OperationCanceledException> (() => task);
        }

        [Test]
        public void DownloadMagnetLink_SameTwice ()
        {
            var link = MagnetLink.Parse ("magnet:?xt=urn:btih:1234512345123451234512345123451234512345");
            using var engine = EngineHelpers.Create (EngineHelpers.CreateSettings ());
            var first = engine.AddAsync (link, "");
            Assert.ThrowsAsync<TorrentException> (() => engine.AddAsync (link, ""));
        }

        [Test]
        public void DownloadMetadata_SameTwice ()
        {
            var link = MagnetLink.Parse ("magnet:?xt=urn:btih:1234512345123451234512345123451234512345");
            using var engine = EngineHelpers.Create (EngineHelpers.CreateSettings ());
            var first = engine.DownloadMetadataAsync (link, CancellationToken.None);
            Assert.ThrowsAsync<TorrentException> (() => engine.DownloadMetadataAsync (link, CancellationToken.None));
        }

        class FakeListener : IPeerConnectionListener
        {
            public IPEndPoint LocalEndPoint { get; set; }
            public IPEndPoint PreferredLocalEndPoint { get; set; }
            public ListenerStatus Status { get; }

#pragma warning disable 0067
            public event EventHandler<PeerConnectionEventArgs> ConnectionReceived;
            public event EventHandler<EventArgs> StatusChanged;
#pragma warning restore 0067

            public FakeListener (int port)
                => (PreferredLocalEndPoint) = (new IPEndPoint (IPAddress.Any, port));

            public void Start ()
            {
            }

            public void Stop ()
            {
            }
        }

        [Test]
        public void GetPortFromListener_ipv4 ()
        {
            var listener = new FakeListener (0);
            var settingsBuilder = new EngineSettingsBuilder (EngineHelpers.CreateSettings ()) { ListenEndPoints = new System.Collections.Generic.Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint (IPAddress.Any, 0) } } };
            var engine = EngineHelpers.Create (settingsBuilder.ToSettings (), EngineHelpers.Factories.WithPeerConnectionListenerCreator (t => listener));
            Assert.AreSame (engine.PeerListeners.Single (), listener);

            // a port of zero isn't an actual listen port. The listener is not bound.
            listener.LocalEndPoint = null;
            listener.PreferredLocalEndPoint = new IPEndPoint (IPAddress.Any, 0);
            Assert.AreEqual (null, engine.GetOverrideOrActualListenPort ("ipv4"));

            // The listener is unbound, but it should eventually bind to 1221
            listener.LocalEndPoint = null;
            listener.PreferredLocalEndPoint = new IPEndPoint (IPAddress.Any, 1221);
            Assert.AreEqual (1221, engine.GetOverrideOrActualListenPort ("ipv4"));

            // The bound port is 1423, the preferred is zero
            listener.LocalEndPoint = new IPEndPoint (IPAddress.Any, 1425);
            listener.PreferredLocalEndPoint = new IPEndPoint (IPAddress.Any, 0);
            Assert.AreEqual (1425, engine.GetOverrideOrActualListenPort ("ipv4"));
        }

        [Test]
        public async Task SaveRestoreState_NoTorrents ()
        {
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings ());
            var restoredEngine = await ClientEngine.RestoreStateAsync (await engine.SaveStateAsync (), engine.Factories);
            Assert.AreEqual (engine.Settings, restoredEngine.Settings);
        }

        [Test]
        [TestCase (true)]
        [TestCase (false)]
        public async Task SaveRestoreState_OneInMemoryTorrent (bool addStreaming)
        {
            var pieceLength = Constants.BlockSize * 4;
            using var tmpDir = TempDir.Create ();

            var torrent = TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary metadata);

            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (cacheDirectory: tmpDir.Path));
            TorrentManager torrentManager;
            if (addStreaming)
                torrentManager = await engine.AddStreamingAsync (torrent, "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = true }.ToSettings ());
            else
                torrentManager = await engine.AddAsync (torrent, "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = true }.ToSettings ());

            await torrentManager.SetFilePriorityAsync (torrentManager.Files[0], Priority.High);
            await torrentManager.MoveFileAsync (torrentManager.Files[1], Path.GetFullPath ("some_fake_path.txt"));

            var restoredEngine = await ClientEngine.RestoreStateAsync (await engine.SaveStateAsync (), engine.Factories);
            Assert.AreEqual (engine.Settings, restoredEngine.Settings);
            Assert.AreEqual (engine.Torrents[0].Torrent.Name, restoredEngine.Torrents[0].Torrent.Name);
            Assert.AreEqual (engine.Torrents[0].SavePath, restoredEngine.Torrents[0].SavePath);
            Assert.AreEqual (engine.Torrents[0].Settings, restoredEngine.Torrents[0].Settings);
            Assert.AreEqual (engine.Torrents[0].InfoHashes, restoredEngine.Torrents[0].InfoHashes);
            Assert.AreEqual (engine.Torrents[0].MagnetLink.ToV1String (), restoredEngine.Torrents[0].MagnetLink.ToV1String ());

            Assert.AreEqual (engine.Torrents[0].Files.Count, restoredEngine.Torrents[0].Files.Count);
            for (int i = 0; i < engine.Torrents.Count; i++) {
                Assert.AreEqual (engine.Torrents[0].Files[i].FullPath, restoredEngine.Torrents[0].Files[i].FullPath);
                Assert.AreEqual (engine.Torrents[0].Files[i].Priority, restoredEngine.Torrents[0].Files[i].Priority);
            }
        }

        [Test]
        [TestCase (true)]
        [TestCase (false)]
        public async Task SaveRestoreState_OneMagnetLink (bool addStreaming)
        {
            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings ());
            if (addStreaming)
                await engine.AddStreamingAsync (new MagnetLink (new InfoHash (new byte[20]), "test"), "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings ());
            else
                await engine.AddAsync (new MagnetLink (new InfoHash (new byte[20]), "test"), "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings ());

            var restoredEngine = await ClientEngine.RestoreStateAsync (await engine.SaveStateAsync (), engine.Factories);
            Assert.AreEqual (engine.Settings, restoredEngine.Settings);
            Assert.AreEqual (engine.Torrents[0].SavePath, restoredEngine.Torrents[0].SavePath);
            Assert.AreEqual (engine.Torrents[0].Settings, restoredEngine.Torrents[0].Settings);
            Assert.AreEqual (engine.Torrents[0].InfoHashes, restoredEngine.Torrents[0].InfoHashes);
            Assert.AreEqual (engine.Torrents[0].MagnetLink.ToV1Uri (), restoredEngine.Torrents[0].MagnetLink.ToV1Uri ());
            Assert.AreEqual (engine.Torrents[0].Files, restoredEngine.Torrents[0].Files);
        }

        [Test]
        public async Task SaveRestoreState_OneTorrentFile_ContainingDirectory ()
        {
            var pieceLength = Constants.BlockSize * 4;
            using var tmpDir = TempDir.Create ();

            TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary metadata);
            var metadataFile = Path.Combine (tmpDir.Path, "test.torrent");
            File.WriteAllBytes (metadataFile, metadata.Encode ());

            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (cacheDirectory: tmpDir.Path));
            var torrentManager = await engine.AddStreamingAsync (metadataFile, "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = true }.ToSettings ());
            await torrentManager.SetFilePriorityAsync (torrentManager.Files[0], Priority.High);
            await torrentManager.MoveFileAsync (torrentManager.Files[1], Path.GetFullPath ("some_fake_path.txt"));

            var restoredEngine = await ClientEngine.RestoreStateAsync (await engine.SaveStateAsync (), engine.Factories);
            Assert.AreEqual (engine.Settings, restoredEngine.Settings);
            Assert.AreEqual (engine.Torrents[0].Torrent.Name, restoredEngine.Torrents[0].Torrent.Name);
            Assert.AreEqual (engine.Torrents[0].SavePath, restoredEngine.Torrents[0].SavePath);
            Assert.AreEqual (engine.Torrents[0].Settings, restoredEngine.Torrents[0].Settings);
            Assert.AreEqual (engine.Torrents[0].InfoHashes, restoredEngine.Torrents[0].InfoHashes);
            Assert.AreEqual (engine.Torrents[0].MagnetLink.ToV1String (), restoredEngine.Torrents[0].MagnetLink.ToV1String ());

            Assert.AreEqual (engine.Torrents[0].Files.Count, restoredEngine.Torrents[0].Files.Count);
            for (int i = 0; i < engine.Torrents.Count; i++) {
                Assert.AreEqual (engine.Torrents[0].Files[i].FullPath, restoredEngine.Torrents[0].Files[i].FullPath);
                Assert.AreEqual (engine.Torrents[0].Files[i].Priority, restoredEngine.Torrents[0].Files[i].Priority);
            }
        }

        [Test]
        public async Task SaveRestoreState_OneTorrentFile_NoContainingDirectory ()
        {
            var pieceLength = Constants.BlockSize * 4;
            using var tmpDir = TempDir.Create ();

            TestRig.CreateMultiFileTorrent (TorrentFile.Create (pieceLength, Constants.BlockSize, Constants.BlockSize * 2, Constants.BlockSize * 3), pieceLength, out BEncoding.BEncodedDictionary metadata);
            var metadataFile = Path.Combine (tmpDir.Path, "test.torrent");
            File.WriteAllBytes (metadataFile, metadata.Encode ());

            var engine = EngineHelpers.Create (EngineHelpers.CreateSettings (cacheDirectory: tmpDir.Path));
            await engine.AddStreamingAsync (metadataFile, "mySaveDirectory", new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings ());

            var restoredEngine = await ClientEngine.RestoreStateAsync (await engine.SaveStateAsync (), engine.Factories);
            Assert.AreEqual (engine.Settings, restoredEngine.Settings);
            Assert.AreEqual (engine.Torrents[0].Torrent.Name, restoredEngine.Torrents[0].Torrent.Name);
            Assert.AreEqual (engine.Torrents[0].SavePath, restoredEngine.Torrents[0].SavePath);
            Assert.AreEqual (engine.Torrents[0].Settings, restoredEngine.Torrents[0].Settings);
            Assert.AreEqual (engine.Torrents[0].InfoHashes, restoredEngine.Torrents[0].InfoHashes);
            Assert.AreEqual (engine.Torrents[0].MagnetLink.ToV1String (), restoredEngine.Torrents[0].MagnetLink.ToV1String ());

            Assert.AreEqual (engine.Torrents[0].Files.Count, restoredEngine.Torrents[0].Files.Count);
            for (int i = 0; i < engine.Torrents.Count; i++) {
                Assert.AreEqual (engine.Torrents[0].Files[i].FullPath, restoredEngine.Torrents[0].Files[i].FullPath);
                Assert.AreEqual (engine.Torrents[0].Files[i].Priority, restoredEngine.Torrents[0].Files[i].Priority);
            }
        }

        [Test]
        public async Task StartAsyncAlwaysCreatesEmptyFiles ()
        {
            var writer = new TestWriter ();
            var files = TorrentFile.Create (Constants.BlockSize * 4, 0, 1, 2, 3);
            using var accessor = TempDir.Create ();
            using var rig = TestRig.CreateMultiFile (files, Constants.BlockSize * 4, writer, baseDirectory: accessor.Path);

            for (int i = 0; i < 2; i++) {

                await rig.Manager.StartAsync ();
                Assert.DoesNotThrowAsync (() => rig.Manager.WaitForState (TorrentState.Downloading), "Started");
                Assert.IsTrue (writer.FilesWithLength.ContainsKey (rig.Manager.Files[0].FullPath));
                Assert.IsTrue (rig.Manager.Files[0].BitField.AllTrue);

                // Files can be moved after they have been created.
                await rig.Manager.MoveFileAsync (rig.Manager.Files[0], rig.Manager.Files[0].FullPath + "new_path");

                await rig.Manager.StopAsync ();
                Assert.DoesNotThrowAsync (() => rig.Manager.WaitForState (TorrentState.Stopped), "Stopped");
                writer.FilesWithLength.Remove (rig.Manager.Files[0].FullPath);
            }
        }

        [Test]
        public async Task StartAsync_DoesNotCreateDoNotDownloadPriority ()
        {
            using var writer = new TestWriter ();
            var files = TorrentFile.Create (Constants.BlockSize * 4, 0, 1, 2, 3);
            using var accessor = TempDir.Create ();
            using var rig = TestRig.CreateMultiFile (files, Constants.BlockSize * 4, writer, baseDirectory: accessor.Path);

            foreach (var file in rig.Manager.Files)
                await rig.Manager.SetFilePriorityAsync (file, Priority.DoNotDownload);

            await rig.Manager.StartAsync ();

            foreach (var file in rig.Manager.Files)
                Assert.IsFalse (await writer.ExistsAsync (file));
        }

        [Test]
        public async Task StartAsync_CreatesAllImplicatedFiles ()
        {
            using var writer = new TestWriter ();
            var files = TorrentFile.Create (Constants.BlockSize * 4, 0, 1, Constants.BlockSize * 4, 3);
            using var accessor = TempDir.Create ();
            using var rig = TestRig.CreateMultiFile (files, Constants.BlockSize * 4, writer, baseDirectory: accessor.Path);

            foreach (var file in rig.Manager.Files)
                await rig.Manager.SetFilePriorityAsync (file, file.Length == 1 ? Priority.Normal : Priority.DoNotDownload);

            await rig.Manager.StartAsync ();
            await rig.Manager.WaitForState (TorrentState.Downloading);

            Assert.IsFalse (await writer.ExistsAsync (rig.Manager.Files[0]));
            Assert.IsTrue (await writer.ExistsAsync (rig.Manager.Files[1]));
            Assert.IsTrue (await writer.ExistsAsync (rig.Manager.Files[2]));
            Assert.IsFalse (await writer.ExistsAsync (rig.Manager.Files[3]));
        }

        [Test]
        public async Task StartAsync_SetPriorityCreatesAllImplicatedFiles ()
        {
            using var writer = new TestWriter ();
            var files = TorrentFile.Create (Constants.BlockSize * 4, 0, 1, Constants.BlockSize * 4, Constants.BlockSize * 4);
            using var accessor = TempDir.Create ();
            using var rig = TestRig.CreateMultiFile (files, Constants.BlockSize * 4, writer, baseDirectory: accessor.Path);

            foreach (var file in rig.Manager.Files)
                await rig.Manager.SetFilePriorityAsync (file, Priority.DoNotDownload);

            await rig.Manager.StartAsync ();

            await rig.Manager.SetFilePriorityAsync (rig.Manager.Files[0], Priority.Normal);
            Assert.IsTrue (await writer.ExistsAsync (rig.Manager.Files[0]));
            Assert.IsTrue (rig.Manager.Files[0].BitField.AllTrue);
            Assert.IsFalse (await writer.ExistsAsync (rig.Manager.Files[1]));

            await rig.Manager.SetFilePriorityAsync (rig.Manager.Files[1], Priority.Normal);
            Assert.IsTrue (await writer.ExistsAsync (rig.Manager.Files[1]));
            Assert.IsTrue (await writer.ExistsAsync (rig.Manager.Files[2]));
            Assert.IsFalse (await writer.ExistsAsync (rig.Manager.Files[3]));
        }

        [Test]
        public async Task StopTest ()
        {
            using var rig = TestRig.CreateMultiFile (new TestWriter ());
            var hashingState = rig.Manager.WaitForState (TorrentState.Hashing);
            var stoppedState = rig.Manager.WaitForState (TorrentState.Stopped);

            await rig.Manager.StartAsync ();
            Assert.IsTrue (hashingState.Wait (5000), "Started");
            await rig.Manager.StopAsync ();
            Assert.IsTrue (stoppedState.Wait (5000), "Stopped");
        }

        [Test]
        public async Task BEP46_UpdateCheckTriggered()
        {
            var dhtMock = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => dhtMock);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(100) }.ToSettings(); // Short interval for testing
            using var engine = new ClientEngine(settings, factories);
            await engine.StartAsync(); // Start the engine loop

            // Add a BEP46 torrent
            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            var link = new MagnetLink(publicKeyHex, null);
            var manager = await engine.AddAsync(link, "savepath");
            await manager.StartAsync(); // Start the manager so it's active

            bool getCalled = false;
            NodeId? receivedTarget = null;
            long? receivedSeq = null;

            dhtMock.GetCallback = (target, seq) => {
                getCalled = true;
                receivedTarget = target;
                receivedSeq = seq;
                return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((null, null, null, null)); // Return no update
            };

            // Wait longer than the interval
            await Task.Delay(300);

            // Force a logic tick (in real use this happens automatically)
            var logicTickMethod = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(logicTickMethod, "Could not find LogicTick method via reflection");
            ClientEngine.MainLoop.QueueWait(() => logicTickMethod.Invoke(engine, null));

            // Wait a moment for the async check to potentially run
            await Task.Delay(100);

            Assert.IsTrue(getCalled, "DhtEngine.GetAsync should have been called");

            var expectedTarget = DhtEngine.CalculateMutableTargetId((BEncodedString)HexDecode(publicKeyHex), null);
            Assert.AreEqual(expectedTarget, receivedTarget, "Target ID mismatch");
            Assert.IsNull(receivedSeq, "Initial sequence number should be null");

            // Verify timer reset (approximate check)
            Assert.IsTrue(manager.LastMutableUpdateCheckTimer.Elapsed < TimeSpan.FromMilliseconds(200), "Timer should have been reset recently");
        }



        [Test]
        public async Task BEP46_UpdateIgnored_SameSequenceNumber()
        {
            var dhtMock = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => dhtMock);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(10) }.ToSettings(); // Very short interval
            using var engine = new ClientEngine(settings, factories);
            await engine.StartAsync();

            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            var link = new MagnetLink(publicKeyHex, null);
            var manager = await engine.AddAsync(link, "savepath");
            await manager.StartAsync();

            // Simulate having already received sequence 1
            var seqProp = manager.GetType().GetProperty("LastKnownSequenceNumber", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(seqProp, "Failed to get LastKnownSequenceNumber property via reflection");
            seqProp.SetValue(manager, (long?)1);

            bool updateEventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => updateEventRaised = true;

            var newInfoHash = InfoHash.FromMemory(new byte[20]); // Dummy infohash
            var updateValue = new BEncodedDictionary { { "ih", new BEncodedString(newInfoHash.AsMemory().ToArray()) } };
            var publicKey = new BEncodedString(HexDecode(publicKeyHex));
            var signature = new BEncodedString(new byte[64]); // Dummy signature

            dhtMock.GetCallback = (target, seq) => {
                // Return the *same* sequence number
                return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((updateValue, publicKey, signature, 1));
            };

            // Trigger check
            await Task.Delay(50); // Wait for interval
            var logicTickMethod = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ClientEngine.MainLoop.QueueWait(() => logicTickMethod.Invoke(engine, null));
            await Task.Delay(50); // Wait for check to run

            Assert.IsFalse(updateEventRaised, "Update event should not be raised for same sequence number");
        }

        [Test]
        public async Task BEP46_UpdateIgnored_LowerSequenceNumber()
        {
            var dhtMock = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => dhtMock);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(10) }.ToSettings();
            using var engine = new ClientEngine(settings, factories);
            await engine.StartAsync();

            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            var link = new MagnetLink(publicKeyHex, null);
            var manager = await engine.AddAsync(link, "savepath");
            await manager.StartAsync();

            // Simulate having already received sequence 2
            var seqProp2 = manager.GetType().GetProperty("LastKnownSequenceNumber", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(seqProp2, "Failed to get LastKnownSequenceNumber property via reflection (test 2)");
            seqProp2.SetValue(manager, (long?)2);

            bool updateEventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => updateEventRaised = true;

            var newInfoHash = InfoHash.FromMemory(new byte[20]);
            var updateValue = new BEncodedDictionary { { "ih", new BEncodedString(newInfoHash.AsMemory().ToArray()) } };
            var publicKey = new BEncodedString(HexDecode(publicKeyHex));
            var signature = new BEncodedString(new byte[64]);

            dhtMock.GetCallback = (target, seq) => {
                // Return a *lower* sequence number
                return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((updateValue, publicKey, signature, 1));
            };

            // Trigger check
            await Task.Delay(50);
            var logicTickMethod = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ClientEngine.MainLoop.QueueWait(() => logicTickMethod.Invoke(engine, null));
            await Task.Delay(50);

            Assert.IsFalse(updateEventRaised, "Update event should not be raised for lower sequence number");
        }

        [Test]
        public async Task BEP46_UpdateIgnored_InvalidSignature()
        {
            var dhtMock = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => dhtMock);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(10) }.ToSettings();
            using var engine = new ClientEngine(settings, factories);
            await engine.StartAsync();

            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            var link = new MagnetLink(publicKeyHex, null);
            var manager = await engine.AddAsync(link, "savepath");
            await manager.StartAsync();

            bool updateEventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => updateEventRaised = true;

            var newInfoHash = InfoHash.FromMemory(new byte[20]);
            var updateValue = new BEncodedDictionary { { "ih", new BEncodedString(newInfoHash.AsMemory().ToArray()) } };
            var publicKey = new BEncodedString(HexDecode(publicKeyHex));
            // Provide a signature known to be invalid (e.g., wrong length or random bytes)
            var invalidSignature = new BEncodedString(new byte[63]); // Incorrect length

            // We rely on VerifyMutableSignature returning false internally.
            // The mock DHT still returns the data as if it was retrieved.
            dhtMock.GetCallback = (target, seq) => {
                return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((updateValue, publicKey, invalidSignature, 1));
            };

            // Trigger check
            await Task.Delay(50);
            var logicTickMethod = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ClientEngine.MainLoop.QueueWait(() => logicTickMethod.Invoke(engine, null));
            await Task.Delay(50);

            Assert.IsFalse(updateEventRaised, "Update event should not be raised for invalid signature");
        }

        [Test]
        public async Task BEP46_UpdateIgnored_InvalidDataFormat()
        {
            var dhtMock = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => dhtMock);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(10) }.ToSettings();
            using var engine = new ClientEngine(settings, factories);
            await engine.StartAsync();

            string publicKeyHex = "8543d3e6115f0f98c944077a4493dcd543e49c739fd998550a1f614ab36ed63e";
            var link = new MagnetLink(publicKeyHex, null);
            var manager = await engine.AddAsync(link, "savepath");
            await manager.StartAsync();

            bool updateEventRaised = false;
            manager.TorrentUpdateAvailable += (s, e) => updateEventRaised = true;

            // Invalid data: missing 'ih' key
            var invalidUpdateValue = new BEncodedDictionary { { "other_key", new BEncodedString("some_value") } };
            var publicKey = new BEncodedString(HexDecode(publicKeyHex));
            var signature = new BEncodedString(new byte[64]); // Assume valid signature for this test

            dhtMock.GetCallback = (target, seq) => {
                // Return the item with invalid format but assume valid signature
                return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((invalidUpdateValue, publicKey, signature, 1));
            };

            // Trigger check
            await Task.Delay(50);
            var logicTickMethod = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ClientEngine.MainLoop.QueueWait(() => logicTickMethod.Invoke(engine, null));
            await Task.Delay(50);

            Assert.IsFalse(updateEventRaised, "Update event should not be raised for invalid data format");
        }




        [Test]
        public async Task BEP46_TwoClient_UpdatePropagation()
        {
            //Console.WriteLine("TEST: Starting BEP46_TwoClient_UpdatePropagation");
            var sharedDht = new ManualDhtEngine();
            var factories = EngineHelpers.Factories.WithDhtCreator(() => sharedDht);
            var settings = new EngineSettingsBuilder { MutableTorrentUpdateInterval = TimeSpan.FromMilliseconds(10) }.ToSettings();

            using var engineA = new ClientEngine(settings, factories);
            using var engineB = new ClientEngine(settings, factories);
            await engineA.StartAsync();
            await engineB.StartAsync();

            // --- Test Setup ---
            // --- Generate real Ed25519 key pair ---
            var random = new SecureRandom();
            var keyPairGenerator = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
            keyPairGenerator.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(random, 256));
            var keyPair = keyPairGenerator.GenerateKeyPair();
            var privateKeyParams = (Ed25519PrivateKeyParameters)keyPair.Private;
            var publicKeyParams = (Ed25519PublicKeyParameters)keyPair.Public;
            var publicKeyABytes = new BEncodedString(publicKeyParams.GetEncoded());
            string publicKeyHexA = BitConverter.ToString(publicKeyParams.GetEncoded()).Replace("-", "").ToLowerInvariant(); // Convert raw bytes to hex
            // We will generate signatures dynamically now.

            // Define minimal realistic 'info' dictionaries for the updates
            var infoDict1 = new BEncodedDictionary {
                { "name", new BEncodedString("test1") },
                { "piece length", new BEncodedNumber(16384) },
                { "pieces", new BEncodedString(new byte[20]) } // Placeholder pieces hash
            };
            var infoDict2 = new BEncodedDictionary {
                { "name", new BEncodedString("test2") },
                { "piece length", new BEncodedNumber(16384) },
                { "pieces", new BEncodedString(Enumerable.Repeat((byte)2, 20).ToArray()) }
            };
            var infoDict3 = new BEncodedDictionary {
                { "name", new BEncodedString("test3") },
                { "piece length", new BEncodedNumber(16384) },
                { "pieces", new BEncodedString(Enumerable.Repeat((byte)3, 20).ToArray()) }
            };

            // Calculate the *actual* InfoHashes corresponding to these dictionaries
            InfoHash actualInfoHash1, actualInfoHash2, actualInfoHash3;
            using (var sha1 = System.Security.Cryptography.SHA1.Create()) {
                actualInfoHash1 = new InfoHash(sha1.ComputeHash(infoDict1.Encode()));
                actualInfoHash2 = new InfoHash(sha1.ComputeHash(infoDict2.Encode()));
                actualInfoHash3 = new InfoHash(sha1.ComputeHash(infoDict3.Encode()));
            }
            Console.WriteLine($"TEST: Calculated actualInfoHash1: {actualInfoHash1.ToHex()}");
            Console.WriteLine($"TEST: Calculated actualInfoHash2: {actualInfoHash2.ToHex()}");
            Console.WriteLine($"TEST: Calculated actualInfoHash3: {actualInfoHash3.ToHex()}");


            // Use these full dictionaries as the 'v' values stored in the mock DHT
            var value1 = infoDict1;
            var value2 = infoDict2;
            var value3 = infoDict3;


            // Initialize mock state
            sharedDht.CurrentSequenceNumber = 0;
            sharedDht.CurrentMutableValue = value1;
            sharedDht.CurrentSalt = null; // Assuming no salt for this test
            sharedDht.CurrentPublicKey = publicKeyABytes;
            sharedDht.CurrentSignature = SignMutableData(privateKeyParams, sharedDht.CurrentSalt, sharedDht.CurrentSequenceNumber, sharedDht.CurrentMutableValue);

            // Mock DHT Get: Returns the current state
            sharedDht.GetCallback = (target, requestedSeq) => {
                try {
                    Console.WriteLine($"TEST: DHT Mock GetCallback invoked. Target: {target}, Requested Seq: {requestedSeq?.ToString() ?? "null"}, Current Mock Seq: {sharedDht.CurrentSequenceNumber}");
                    Console.WriteLine($"TEST: DHT Mock GetCallback invoked. Target: {target}, Requested Seq: {requestedSeq?.ToString() ?? "null"}, Current Mock Seq: {sharedDht.CurrentSequenceNumber}");
                    // Simulate DHT filtering based on sequence number
                    if (requestedSeq.HasValue && sharedDht.CurrentSequenceNumber <= requestedSeq.Value)
                    {
                        // BEP44: If requestedSeq >= storedSeq, return *only* the stored sequence number.
                        (BEncodedValue?, BEncodedString?, BEncodedString?, long?) resultTuple = (null, null, null, (long?)sharedDht.CurrentSequenceNumber);
                        Console.WriteLine($"TEST: DHT Mock returning only seq: {resultTuple.Item4}");
                        Console.WriteLine($"TEST: DHT Mock returning only seq: {resultTuple.Item4}");
                        return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>(resultTuple);
                    }
                    var fullResultTuple = (sharedDht.CurrentMutableValue, sharedDht.CurrentPublicKey, sharedDht.CurrentSignature, (long?)sharedDht.CurrentSequenceNumber);
                    Console.WriteLine($"TEST: DHT Mock returning full data: Seq={fullResultTuple.Item4}, ValuePresent={fullResultTuple.Item1!=null}, PKPresent={fullResultTuple.Item2!=null}, SigPresent={fullResultTuple.Item3!=null}");
                    Console.WriteLine($"TEST: DHT Mock returning full data: Seq={fullResultTuple.Item4}");
                    return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>(fullResultTuple);
                } catch (Exception ex) {
                    Console.WriteLine($"!!!!!!!!!!!! EXCEPTION IN GetCallback: {ex.Message} !!!!!!!!!!!!");
                    Console.WriteLine($"!!!!!!!!!!!! EXCEPTION IN GetCallback: {ex.Message} !!!!!!!!!!!!");
                    return Task.FromResult<(BEncodedValue?, BEncodedString?, BEncodedString?, long?)>((null, null, null, null)); // Return null on error
                }
            };

            // --- Client B subscribes ---
            //Console.WriteLine("TEST: Client B subscribing...");
            Console.WriteLine("TEST: Client B subscribing...");
            var link = new MagnetLink(publicKeyHexA, null);
            var managerB = await engineB.AddAsync(link, "savepathB");
            await managerB.StartAsync();
            // Trigger an initial check to process sequence 0 if necessary and wait for it
            Console.WriteLine("TEST: Triggering initial LogicTick for B...");
            //Console.WriteLine("TEST: Triggering initial LogicTick for B...");
            var logicTickMethodB = typeof(ClientEngine).GetMethod("LogicTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(logicTickMethodB, "Could not find LogicTick method via reflection");
            var initialTickTcs = new TaskCompletionSource<object?>(); // Use TaskCompletionSource
            ClientEngine.MainLoop.QueueWait(() => { // Queue the action
                try {
                    logicTickMethodB.Invoke(engineB, null);
                    initialTickTcs.TrySetResult(null); // Signal completion
                } catch (Exception ex) {
                    initialTickTcs.TrySetException(ex); // Signal error if invoke fails
                }
            });
            var initialTickTask = initialTickTcs.Task; // Get the Task to await
            await initialTickTask.WithTimeout(TimeSpan.FromSeconds(1)); // Ensure the tick runs
            Console.WriteLine("TEST: Initial LogicTick for B completed.");
            //Console.WriteLine("TEST: Initial LogicTick for B completed.");
            await Task.Delay(50); // Allow subsequent async actions from the tick to settle


            // --- Client A publishes update 1 (seq 1) ---
            Console.WriteLine($"TEST: Client A publishing update 1 (seq 1). Mock DHT State: Seq={sharedDht.CurrentSequenceNumber}, Value={sharedDht.CurrentMutableValue?.ToString() ?? "null"}");
            //Console.WriteLine("TEST: Client A publishing update 1 (seq 1)");
            ClientEngine.MainLoop.QueueWait (() => {
                sharedDht.CurrentSequenceNumber = 1;
                sharedDht.CurrentMutableValue = value2;
                sharedDht.CurrentSignature = SignMutableData(privateKeyParams, sharedDht.CurrentSalt, sharedDht.CurrentSequenceNumber, sharedDht.CurrentMutableValue); // Recalculate signature
            });

            // --- Client B receives update 1 ---
            InfoHash? receivedInfoHash1 = null;
            long? receivedSeq1 = null;
            var update1Tcs = new TaskCompletionSource<bool>();
            Console.WriteLine("TEST: Setting up handler for update 1");
            //Console.WriteLine("TEST: Setting up handler for update 1");
            managerB.TorrentUpdateAvailable += (s, e) => {
                Console.WriteLine($"TEST: TorrentUpdateAvailable handler (1) invoked for manager {e.Manager.GetHashCode()} with hash {e.NewInfoHash}, CurrentKnownSeq={managerB.LastKnownSequenceNumber}");
                //Console.WriteLine($"TEST: TorrentUpdateAvailable handler (1) invoked for manager {e.Manager.GetHashCode()} with hash {e.NewInfoHash}");
                // Check against the *actual* calculated InfoHash for the second dictionary
                if (e.Manager == managerB && e.NewInfoHash == actualInfoHash2)
                {
                    receivedInfoHash1 = e.NewInfoHash;
                    receivedSeq1 = managerB.LastKnownSequenceNumber; // Check internal state after update
                    Console.WriteLine($"TEST: Update 1 MATCH. TCS SetResult. Seq: {managerB.LastKnownSequenceNumber}");
                    //Console.WriteLine($"TEST: Update 1 TCS SetResult. Seq: {managerB.LastKnownSequenceNumber}");
                    update1Tcs.TrySetResult(true);
                }
            };

            // Trigger check on B directly
            Console.WriteLine("TEST: Triggering PerformMutableUpdateCheckAsync for update 1 on B...");
            var checkMethod = managerB.GetType().GetMethod("PerformMutableUpdateCheckAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(checkMethod, "Could not find PerformMutableUpdateCheckAsync method via reflection");
            ClientEngine.MainLoop.QueueWait (() => {
                // We need to invoke the method and await the ReusableTask it returns,
                // but QueueWait is synchronous. We'll fire-and-forget the task within the loop.
                 _ = checkMethod.Invoke(managerB, null);
            });

            // Wait for B to receive the update
            Console.WriteLine($"TEST: Awaiting update 1 TCS ({update1Tcs.Task.Status})...");
            //Console.WriteLine($"TEST: Awaiting update 1 TCS ({update1Tcs.Task.Status})...");
            bool receivedUpdate1 = await update1Tcs.Task.WithTimeout(TimeSpan.FromSeconds(5));
            Console.WriteLine($"TEST: Update 1 received: {receivedUpdate1}");
            //Console.WriteLine($"TEST: Update 1 received: {receivedUpdate1}");

            Assert.IsTrue(receivedUpdate1, "Client B should have received update 1");
            Assert.AreEqual(actualInfoHash2, receivedInfoHash1, "Client B received wrong infohash for update 1");
            Assert.AreEqual(1, receivedSeq1, "Client B has wrong sequence number after update 1");

            // --- Client A publishes update 2 (seq 2) ---
            Console.WriteLine($"TEST: Client A publishing update 2 (seq 2). Mock DHT State: Seq={sharedDht.CurrentSequenceNumber}, Value={sharedDht.CurrentMutableValue?.ToString() ?? "null"}");
            //Console.WriteLine("TEST: Client A publishing update 2 (seq 2)");
             ClientEngine.MainLoop.QueueWait (() => {
                sharedDht.CurrentSequenceNumber = 2;
                sharedDht.CurrentMutableValue = value3;
                sharedDht.CurrentSignature = SignMutableData(privateKeyParams, sharedDht.CurrentSalt, sharedDht.CurrentSequenceNumber, sharedDht.CurrentMutableValue); // Recalculate signature
            });

            // --- Client B receives update 2 ---
            InfoHash? receivedInfoHash2 = null;
            long? receivedSeq2 = null;
            var update2Tcs = new TaskCompletionSource<bool>();
            Console.WriteLine("TEST: Setting up handler for update 2");
            //Console.WriteLine("TEST: Setting up handler for update 2");
            managerB.TorrentUpdateAvailable += (s, e) => { // Re-hook or use a flag
                 Console.WriteLine($"TEST: TorrentUpdateAvailable handler (2) invoked for manager {e.Manager.GetHashCode()} with hash {e.NewInfoHash}, CurrentKnownSeq={managerB.LastKnownSequenceNumber}");
                 //Console.WriteLine($"TEST: TorrentUpdateAvailable handler (2) invoked for manager {e.Manager.GetHashCode()} with hash {e.NewInfoHash}");
                 // Check against the *actual* calculated InfoHash for the third dictionary
                 if (e.Manager == managerB && e.NewInfoHash == actualInfoHash3)
                 {
                    receivedInfoHash2 = e.NewInfoHash;
                    receivedSeq2 = managerB.LastKnownSequenceNumber;
                    Console.WriteLine($"TEST: Update 2 MATCH. TCS SetResult. Seq: {managerB.LastKnownSequenceNumber}");
                    //Console.WriteLine($"TEST: Update 2 TCS SetResult. Seq: {managerB.LastKnownSequenceNumber}");
                    update2Tcs.TrySetResult(true);
                 }
            };

            // Trigger check on B directly again
            Console.WriteLine("TEST: Triggering PerformMutableUpdateCheckAsync for update 2 on B...");
            ClientEngine.MainLoop.QueueWait (() => {
                 _ = checkMethod.Invoke(managerB, null);
            });
            // Add a small delay to allow the queued actions and the resulting event to propagate
            await Task.Delay(100);

            // Wait for B to receive the second update
            Console.WriteLine($"TEST: Awaiting update 2 TCS ({update2Tcs.Task.Status})...");
            //Console.WriteLine($"TEST: Awaiting update 2 TCS ({update2Tcs.Task.Status})...");
            bool receivedUpdate2 = await update2Tcs.Task.WithTimeout(TimeSpan.FromSeconds(5)); // This is the line that timed out (previously 907, now adjusted)
            Console.WriteLine($"TEST: Update 2 received: {receivedUpdate2}");
            //Console.WriteLine($"TEST: Update 2 received: {receivedUpdate2}");

            Assert.IsTrue(receivedUpdate2, "Client B should have received update 2");
            Assert.AreEqual(actualInfoHash3, receivedInfoHash2, "Client B received wrong infohash for update 2");
            Assert.AreEqual(2, receivedSeq2, "Client B has wrong sequence number after update 2");
            Console.WriteLine("TEST: Finished BEP46_TwoClient_UpdatePropagation");
            //Console.WriteLine("TEST: Finished BEP46_TwoClient_UpdatePropagation");
        }


        // Helper function to sign mutable data for BEP46 tests
        static BEncodedString SignMutableData(Ed25519PrivateKeyParameters privateKey, BEncodedString? salt, long sequenceNumber, BEncodedValue value)
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
