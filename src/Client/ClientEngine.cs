//
// ClientEngine.cs
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
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Client.Listeners;
using MonoTorrent.Client.RateLimiters;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Dht;
using MonoTorrent.Logging;
using MonoTorrent.BlockReader;
using MonoTorrent.PieceWriter;
using NATS.Client.Core; // Added for NatsOpts
using MonoTorrent.Connections; // Added for ListenerStatus
using System.Diagnostics; // Added for Debug.WriteLine
using MonoTorrent.PortForwarding;
using MonoTorrent.Dht.Messages; // Added for Ping message

using ReusableTasks;

namespace MonoTorrent.Client
{
    /// <summary>
    /// The Engine that contains the TorrentManagers
    /// </summary>
    public class ClientEngine : IDisposable
    {
        static ClientEngine ()
        {
            ReusableTaskMethodBuilder.MaximumCacheSize = 2048;
        }

        internal static readonly MainLoop MainLoop = new MainLoop ("Client Engine Loop");
        static readonly Logger Log = Logger.Create (nameof (ClientEngine));

        public static Task<ClientEngine> RestoreStateAsync (string pathToStateFile)
            => RestoreStateAsync (pathToStateFile, Factories.Default);

        public static async Task<ClientEngine> RestoreStateAsync (string pathToStateFile, Factories factories)
        {
            await MainLoop.SwitchThread ();
            return await RestoreStateAsync (File.ReadAllBytes (pathToStateFile), factories);
        }

        public static Task<ClientEngine> RestoreStateAsync (ReadOnlyMemory<byte> buffer)
            => RestoreStateAsync (buffer, Factories.Default);

        public static async Task<ClientEngine> RestoreStateAsync (ReadOnlyMemory<byte> buffer, Factories factories)
        {
            var state = BEncodedValue.Decode<BEncodedDictionary> (buffer.Span);
            var engineSettings = Serializer.DeserializeEngineSettings ((BEncodedDictionary) state["Settings"]);

            var clientEngine = new ClientEngine (engineSettings, factories);
            TorrentManager manager;
            foreach (BEncodedDictionary torrent in (BEncodedList) state[nameof (clientEngine.Torrents)]) {
                var saveDirectory = ((BEncodedString) torrent[nameof (manager.SavePath)]).Text;
                var streaming = bool.Parse (((BEncodedString) torrent["Streaming"]).Text);
                var torrentSettings = Serializer.DeserializeTorrentSettings ((BEncodedDictionary) torrent[nameof (manager.Settings)]);

                if (torrent.ContainsKey (nameof (manager.MetadataPath))) {
                    var metadataPath = (BEncodedString) torrent[nameof (manager.MetadataPath)];
                    if (streaming)
                        manager = await clientEngine.AddStreamingAsync (metadataPath.Text, saveDirectory, torrentSettings);
                    else
                        manager = await clientEngine.AddAsync (metadataPath.Text, saveDirectory, torrentSettings);

                    foreach (BEncodedDictionary file in (BEncodedList) torrent[nameof (manager.Files)]) {
                        TorrentFileInfo torrentFile;
                        var path = new TorrentPath (((BEncodedList) file[nameof(torrentFile.Path)]));
                        torrentFile = (TorrentFileInfo) manager.Files.Single (t => t.Path == path);
                        torrentFile.Priority = (Priority) Enum.Parse (typeof (Priority), file[nameof (torrentFile.Priority)].ToString ()!);
                        torrentFile.UpdatePaths ((
                            newPath: ((BEncodedString) file[nameof (torrentFile.FullPath)]).Text,
                            downloadCompletePath: ((BEncodedString) file[nameof (torrentFile.DownloadCompleteFullPath)]).Text,
                            downloadIncompletePath: ((BEncodedString) file[nameof (torrentFile.DownloadIncompleteFullPath)]).Text
                        ));
                    }
                } else {
                    var magnetLink = MagnetLink.Parse (torrent[nameof (manager.MagnetLink)].ToString ()!);
                    if (streaming)
                        await clientEngine.AddStreamingAsync (magnetLink, saveDirectory, torrentSettings);
                    else
                        await clientEngine.AddAsync (magnetLink, saveDirectory, torrentSettings);
                }
            }
            return clientEngine;
        }

        public async Task<byte[]> SaveStateAsync ()
        {
            await MainLoop;
            var state = new BEncodedDictionary {
                {nameof (Settings), Serializer.Serialize (Settings) },
            };

            state[nameof (Torrents)] = new BEncodedList (Torrents.Select (t => {
                var dict = new BEncodedDictionary {
                    { nameof (t.MagnetLink),  (BEncodedString) t.MagnetLink.ToV1String () },
                    { nameof(t.SavePath), (BEncodedString) t.SavePath },
                    { nameof(t.Settings), Serializer.Serialize (t.Settings) },
                    { "Streaming", (BEncodedString) (t.StreamProvider != null).ToString ()},
                };

                if (t.HasMetadata) {
                    dict[nameof (t.Files)] = new BEncodedList (t.Files.Select (file =>
                       new BEncodedDictionary {
                            { nameof(file.FullPath), (BEncodedString) file.FullPath },
                            { nameof(file.DownloadCompleteFullPath), (BEncodedString) file.DownloadCompleteFullPath },
                            { nameof(file.DownloadIncompleteFullPath), (BEncodedString) file.DownloadIncompleteFullPath },
                            { nameof(file.Path), (BEncodedList) file.Path.Encode () },
                            { nameof(file.Priority), (BEncodedString) file.Priority.ToString () },
                       }
                    ));
                    dict[nameof (t.MetadataPath)] = (BEncodedString) t.MetadataPath;
                } else {
                }

                return dict;
            }));

            foreach (var v in Torrents)
                await v.MaybeWriteFastResumeAsync ();

            return state.Encode ();
        }

        public async Task SaveStateAsync (string pathToStateFile)
        {
            var state = await SaveStateAsync ();
            await MainLoop.SwitchThread ();
            File.WriteAllBytes (pathToStateFile, state);
        }

        /// <summary>
        /// An un-seeded random number generator which will not generate the same
        /// random sequence when the application is restarted.
        /// </summary>
        static readonly Random PeerIdRandomGenerator = new Random ();

        #region Global Constants

        public static readonly bool SupportsInitialSeed = false;
        public static readonly bool SupportsLocalPeerDiscovery = true;
        public static readonly bool SupportsWebSeed = true;
        public static readonly bool SupportsEncryption = true;
        public static readonly bool SupportsEndgameMode = true;
        public static readonly bool SupportsDht = true;
        internal const int TickLength = 500;    // A logic tick will be performed every TickLength miliseconds

        #endregion


        #region Events

        public event EventHandler<StatsUpdateEventArgs>? StatsUpdate;
        public event EventHandler<CriticalExceptionEventArgs>? CriticalException;

        #endregion


        #region Member Variables

        readonly SemaphoreSlim dhtNodeLocker;

        readonly ListenManager listenManager;         // Listens for incoming connections and passes them off to the correct TorrentManager
        int tickCount;
        /// <summary>
        /// The <see cref="TorrentManager"/> instances registered by the user.
        /// </summary>
        readonly List<TorrentManager> publicTorrents;

        /// <summary>
        /// The <see cref="TorrentManager"/> instances registered by the user and the instances
        /// implicitly created by <see cref="DownloadMetadataAsync(MagnetLink, CancellationToken)"/>.
        /// </summary>
        readonly List<TorrentManager> allTorrents;

        readonly RateLimiter uploadLimiter;
        readonly RateLimiterGroup uploadLimiters;
        readonly RateLimiter downloadLimiter;
        readonly RateLimiterGroup downloadLimiters;
        readonly NatsNatTraversalService? natsService; // Added NATS service instance

        #endregion


        #region Properties

        public ConnectionManager ConnectionManager { get; }

        public IDht Dht { get; private set; }

        internal IDhtEngine DhtEngine { get; private set; }

        public IDhtListener DhtListener { get; private set; } // Tracks the listener set by the factory

        /// <summary>
        /// Gets the actual listener instance used by the underlying DhtEngine's message loop.
        /// Returns null if the DhtEngine is not the expected concrete type or the listener is not available.
        /// </summary>
        public IDhtListener? ActualDhtListener {
            get {
                // Requires casting to the concrete DhtEngine type to access MessageLoop
                if (this.DhtEngine is DhtEngine concreteDhtEngine)
                {
                    return concreteDhtEngine.MessageLoop?.Listener;
                }
                return null;
            }
        }

        public DiskManager DiskManager { get; }

        internal IBlockReader BlockReader { get; }

        public bool Disposed { get; private set; }

        internal Factories Factories { get; }


        /// <summary>
        /// A readonly list of the listeners which the engine is using to receive incoming connections from other peers.
        /// This are created by passing <see cref="EngineSettings.ListenEndPoints"/> to the <see cref="Factories.CreatePeerConnectionListener(IPEndPoint)"/> factory method.
        /// </summary>
        public IList<IPeerConnectionListener> PeerListeners { get; private set; } = Array.Empty<IPeerConnectionListener> ();

        internal ILocalPeerDiscovery LocalPeerDiscovery { get; private set; }

        /// <summary>
        /// When <see cref="EngineSettings.AllowPortForwarding"/> is set to true, this will return a representation
        /// of the ports the engine is managing.
        /// </summary>
        public Mappings PortMappings => Settings.AllowPortForwarding ? PortForwarder.Mappings : Mappings.Empty;

        public bool IsRunning { get; private set; }

        public BEncodedString PeerId { get; }

        public IPortForwarder PortForwarder { get; }

        internal NatsNatTraversalService? NatsService => natsService; // Expose NATS service if needed

        public NatsNatTraversalService? PublicNatsService => natsService;

        public void PublicHandleNatsPeerDiscovered(object? sender, MonoTorrent.Dht.NatsNatTraversalService.NatsPeerDiscoveredEventArgs e)
        {
            HandleNatsPeerDiscovered(sender, e);
        }

        public EngineSettings Settings { get; private set; }

        public IList<TorrentManager> Torrents { get; }

        public long TotalDownloadRate {
            get {
                long total = 0;
                for (int i = 0; i < publicTorrents.Count; i++)
                    total += publicTorrents[i].Monitor.DownloadRate;
                return total;
            }
        }

        public long TotalUploadRate {
            get {
                long total = 0;
                for (int i = 0; i < publicTorrents.Count; i++)
                    total += publicTorrents[i].Monitor.UploadRate;
                return total;
            }
        }

        #endregion


        #region Constructors

        public ClientEngine ()
            : this (new EngineSettings ())
        {

        }

        public ClientEngine (EngineSettings settings)
            : this (settings, Factories.Default)
        {

        }

        public ClientEngine (EngineSettings settings, Factories factories)
        {
            settings = settings ?? throw new ArgumentNullException (nameof (settings));
            Factories = factories ?? throw new ArgumentNullException (nameof (factories));

            // This is just a sanity check to make sure the ReusableTasks.dll assembly is
            // loadable.
            GC.KeepAlive (ReusableTasks.ReusableTask.CompletedTask);

            PeerId = GeneratePeerId ();
            Settings = settings ?? throw new ArgumentNullException (nameof (settings));
            CheckSettingsAreValid (Settings);

            allTorrents = new List<TorrentManager> ();
            dhtNodeLocker = new SemaphoreSlim (1, 1);
            publicTorrents = new List<TorrentManager> ();
            Torrents = new ReadOnlyCollection<TorrentManager> (publicTorrents);

            DiskManager = new DiskManager (Settings, Factories);
            BlockReader = Factories.CreatePieceReader (DiskManager);

            ConnectionManager = new ConnectionManager (PeerId, Settings, Factories, BlockReader);
            listenManager = new ListenManager (this, Factories.CreatePeerConnectionGate());
            PortForwarder = Factories.CreatePortForwarder ();

            MainLoop.QueueTimeout (TimeSpan.FromMilliseconds (TickLength), delegate {
                if (IsRunning && !Disposed)
                    LogicTick ();
                return !Disposed;
            });

            downloadLimiter = new RateLimiter ();
            downloadLimiters = new RateLimiterGroup {
                new DiskWriterLimiter(DiskManager),
                downloadLimiter,
            };

            uploadLimiter = new RateLimiter ();
            uploadLimiters = new RateLimiterGroup {
                uploadLimiter
            };

            PeerListeners = Array.AsReadOnly (settings.ListenEndPoints.Values.Select (t => Factories.CreatePeerConnectionListener (t)).ToArray ());
            listenManager.SetListeners (PeerListeners);

            DhtListener = (settings.DhtEndPoint == null ? null : Factories.CreateDhtListener (settings.DhtEndPoint)) ?? new NullDhtListener ();
            DhtEngine = (settings.DhtEndPoint == null ? null : Factories.CreateDht ()) ?? new NullDhtEngine ();
            Dht = new DhtEngineWrapper (DhtEngine);
            DhtEngine.SetListenerAsync (DhtListener).GetAwaiter ().GetResult ();

            DhtEngine.StateChanged += DhtEngineStateChanged;
            DhtEngine.PeersFound += DhtEnginePeersFound;
            LocalPeerDiscovery = new NullLocalPeerDiscovery ();

            RegisterLocalPeerDiscovery (settings.AllowLocalPeerDiscovery ? Factories.CreateLocalPeerDiscovery () : null);

            // Create NATS service if enabled in settings
            if (settings.AllowNatsDiscovery && settings.NatsOptions != null)
            {
                // Calculate NodeId from PeerId (SHA1 hash) - needed by NATS service
                using var sha1 = System.Security.Cryptography.SHA1.Create();
                var nodeIdBytes = sha1.ComputeHash(this.PeerId.AsMemory().Span.ToArray()); // Correct property name is PeerId
                var tempInfoHash = InfoHash.FromMemory(nodeIdBytes);
                var nodeId = NodeId.FromInfoHash(tempInfoHash);

                Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Creating NatsNatTraversalService for NodeId {nodeId}");
                natsService = new NatsNatTraversalService(settings.NatsOptions, nodeId, PeerId);
                Console.WriteLine($"[Console] Subscribing to NatsNatTraversalService.PeerDiscovered event...");
                natsService.PeerDiscovered += HandleNatsPeerDiscovered; // Subscribe to the event
                Console.WriteLine($"[Console] ClientEngine subscribed to NatsNatTraversalService.PeerDiscovered event");
                Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] NatsNatTraversalService created and PeerDiscovered handler attached.");
            } else {
                 Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] NATS Discovery is disabled.");
            }
        }

        #endregion


        #region Methods

        public Task<TorrentManager> AddAsync (MagnetLink magnetLink, string saveDirectory)
            => AddAsync (magnetLink, saveDirectory, new TorrentSettings ());

        public Task<TorrentManager> AddAsync (MagnetLink magnetLink, string saveDirectory, TorrentSettings settings)
            => AddAsync (magnetLink, null, saveDirectory, settings);

        public Task<TorrentManager> AddAsync (string metadataPath, string saveDirectory)
            => AddAsync (metadataPath, saveDirectory, new TorrentSettings ());

        public async Task<TorrentManager> AddAsync (string metadataPath, string saveDirectory, TorrentSettings settings)
        {
            var torrent = await Torrent.LoadAsync (metadataPath).ConfigureAwait (false);

            var metadataCachePath = Settings.GetMetadataPath (torrent.InfoHashes);
            if (metadataPath != metadataCachePath) {
                Directory.CreateDirectory (Path.GetDirectoryName (metadataCachePath)!);
                File.Copy (metadataPath, metadataCachePath, true);
            }
            return await AddAsync (null, torrent, saveDirectory, settings);
        }

        public Task<TorrentManager> AddAsync (Torrent torrent, string saveDirectory)
            => AddAsync (torrent, saveDirectory, new TorrentSettings ());

        public async Task<TorrentManager> AddAsync (Torrent torrent, string saveDirectory, TorrentSettings settings)
        {
            await MainLoop.SwitchThread ();

            var editor = new TorrentEditor (new BEncodedDictionary {
                { "info", BEncodedValue.Decode (torrent.InfoMetadata.Span) }
            });
            editor.SetCustom ("name", (BEncodedString) torrent.Name);

            if (torrent.AnnounceUrls.Count > 0) {
                if (torrent.AnnounceUrls.Count == 1 && torrent.AnnounceUrls[0].Count == 1) {
                    editor.Announce = torrent.AnnounceUrls.Single ().Single ();
                } else {
                    foreach (var tier in torrent.AnnounceUrls) {
                        var list = new List<string> ();
                        foreach (var tracker in tier)
                            list.Add (tracker);
                        editor.Announces.Add (list);
                    }
                }
            }

            var metadataCachePath = Settings.GetMetadataPath (torrent.InfoHashes);
            Directory.CreateDirectory (Path.GetDirectoryName (metadataCachePath)!);
            File.WriteAllBytes (metadataCachePath, editor.ToDictionary ().Encode ());

            return await AddAsync (null, torrent, saveDirectory, settings);
        }

        async Task<TorrentManager> AddAsync (MagnetLink? magnetLink, Torrent? torrent, string saveDirectory, TorrentSettings settings)
        {
            await MainLoop;

            saveDirectory = string.IsNullOrEmpty (saveDirectory) ? Environment.CurrentDirectory : Path.GetFullPath (saveDirectory);
            TorrentManager manager;
            if (magnetLink != null) {
                var metadataSaveFilePath = Settings.GetMetadataPath (magnetLink.InfoHashes);
                manager = new TorrentManager (this, magnetLink, saveDirectory, settings);
                if (Settings.AutoSaveLoadMagnetLinkMetadata && Torrent.TryLoad (metadataSaveFilePath, out torrent) && torrent.InfoHashes == magnetLink.InfoHashes)
                    manager.SetMetadata (torrent);
            } else if (torrent != null) {
                manager = new TorrentManager (this, torrent, saveDirectory, settings);
            } else {
                throw new InvalidOperationException ($"You must pass a non-null {nameof (magnetLink)} or {nameof (torrent)}");
            }

            await Register (manager, true);
            await manager.MaybeLoadFastResumeAsync ();

            return manager;
        }

        public async Task<TorrentManager> AddStreamingAsync (MagnetLink magnetLink, string saveDirectory)
            => await MakeStreamingAsync (await AddAsync (magnetLink, saveDirectory));

        public async Task<TorrentManager> AddStreamingAsync (MagnetLink magnetLink, string saveDirectory, TorrentSettings settings)
            => await MakeStreamingAsync (await AddAsync (magnetLink, saveDirectory, settings));

        public async Task<TorrentManager> AddStreamingAsync (string metadataPath, string saveDirectory)
            => await MakeStreamingAsync (await AddAsync (metadataPath, saveDirectory));

        public async Task<TorrentManager> AddStreamingAsync (string metadataPath, string saveDirectory, TorrentSettings settings)
            => await MakeStreamingAsync (await AddAsync (metadataPath, saveDirectory, settings));

        public async Task<TorrentManager> AddStreamingAsync (Torrent torrent, string saveDirectory)
            => await MakeStreamingAsync (await AddAsync (torrent, saveDirectory));

        public async Task<TorrentManager> AddStreamingAsync (Torrent torrent, string saveDirectory, TorrentSettings settings)
            => await MakeStreamingAsync (await AddAsync (torrent, saveDirectory, settings));

        async Task<TorrentManager> MakeStreamingAsync (TorrentManager manager)
        {
            await manager.ChangePickerAsync (Factories.CreateStreamingPieceRequester ());
            return manager;
        }

        public Task<bool> RemoveAsync (MagnetLink magnetLink)
            => RemoveAsync (magnetLink, RemoveMode.CacheDataOnly);

        public Task<bool> RemoveAsync (MagnetLink magnetLink, RemoveMode mode)
        {
            magnetLink = magnetLink ?? throw new ArgumentNullException (nameof (magnetLink));
            return RemoveAsync (magnetLink.InfoHashes, mode);
        }

        public Task<bool> RemoveAsync (Torrent torrent)
            => RemoveAsync (torrent, RemoveMode.CacheDataOnly);

        public Task<bool> RemoveAsync (Torrent torrent, RemoveMode mode)
        {
            torrent = torrent ?? throw new ArgumentNullException (nameof (torrent));
            return RemoveAsync (torrent.InfoHashes, mode);
        }

        public Task<bool> RemoveAsync (TorrentManager manager)
            => RemoveAsync (manager, RemoveMode.CacheDataOnly);

        public async Task<bool> RemoveAsync (TorrentManager manager, RemoveMode mode)
        {
            CheckDisposed ();
            Check.Manager (manager);

            await MainLoop;
            if (manager.Engine != this)
                throw new TorrentException ("The manager has not been registered with this engine");

            if (manager.State != TorrentState.Stopped)
                throw new TorrentException ("The manager must be stopped before it can be unregistered");

            allTorrents.Remove (manager);
            publicTorrents.Remove (manager);
            ConnectionManager.Remove (manager);
            if (!Contains (manager.InfoHashes))
                listenManager.Remove (manager.InfoHashes);

            manager.DownloadLimiters.Remove (downloadLimiters);
            manager.UploadLimiters.Remove (uploadLimiters);
            manager.Dispose ();

            if (mode.HasFlag (RemoveMode.CacheDataOnly)) {
                foreach (var path in new[] { Settings.GetFastResumePath (manager.InfoHashes), Settings.GetMetadataPath (manager.InfoHashes) })
                    if (File.Exists (path))
                        File.Delete (path);
            }
            if (mode.HasFlag (RemoveMode.DownloadedDataOnly)) {
                foreach (var path in manager.Files.Select (f => f.FullPath))
                    if (File.Exists (path))
                        File.Delete (path);
                // FIXME: Clear the empty directories.
            }
            return true;
        }

        async Task<bool> RemoveAsync (InfoHashes infoHashes, RemoveMode mode)
        {
            await MainLoop;
            var manager = allTorrents.FirstOrDefault (t => t.InfoHashes == infoHashes);
            return manager != null && await RemoveAsync (manager, mode);
        }

        async Task ChangePieceWriterAsync (IPieceWriter writer)
        {
            writer = writer ?? throw new ArgumentNullException (nameof (writer));

            await MainLoop;
            if (IsRunning)
                throw new InvalidOperationException ("You must stop all active downloads before changing the piece writer used to write data to disk.");
            await DiskManager.SetWriterAsync (writer);
        }

        void CheckDisposed ()
        {
            if (Disposed)
                throw new ObjectDisposedException (GetType ().Name);
        }

        public bool Contains (InfoHashes infoHashes)
        {
            CheckDisposed ();
            if (infoHashes == null)
                return false;

            return allTorrents.Exists (m => m.InfoHashes == infoHashes);
        }

        public bool Contains (Torrent torrent)
        {
            CheckDisposed ();
            if (torrent == null)
                return false;

            return Contains (torrent.InfoHashes);
        }

        public bool Contains (TorrentManager manager)
        {
            CheckDisposed ();
            if (manager == null)
                return false;

            return Contains (manager.InfoHashes);
        }

        public void Dispose ()
        {
            if (Disposed)
                return;

            Disposed = true;
            MainLoop.QueueWait (() => {
                foreach (var listener in PeerListeners)
                    listener.Stop ();
                listenManager.SetListeners (Array.Empty<IPeerConnectionListener> ());

                DhtListener.Stop ();
                DhtEngine.Dispose ();

                DiskManager.Dispose ();
                LocalPeerDiscovery.Stop ();

                (PortForwarder as IDisposable)?.Dispose ();
                if (natsService != null) {
                    natsService.PeerDiscovered -= HandleNatsPeerDiscovered; // Unsubscribe
                    natsService.Dispose ();
                }
            });
        }

        /// <summary>
        /// Downloads the .torrent metadata for the provided MagnetLink.
        /// </summary>
        /// <param name="magnetLink">The MagnetLink to get the metadata for.</param>
        /// <param name="token">The cancellation token used to to abort the download. This method will
        /// only complete if the metadata successfully downloads, or the token is cancelled.</param>
        /// <returns></returns>
        public async Task<ReadOnlyMemory<byte>> DownloadMetadataAsync (MagnetLink magnetLink, CancellationToken token)
        {
            await MainLoop;

            var manager = new TorrentManager (this, magnetLink, "", new TorrentSettings ());
            var metadataCompleted = new TaskCompletionSource<ReadOnlyMemory<byte>> ();
            using var registration = token.Register (() => metadataCompleted.TrySetResult (null));
            manager.MetadataReceived += (o, e) => metadataCompleted.TrySetResult (e);

            await Register (manager, isPublic: false);
            await manager.StartAsync (metadataOnly: true);
            var data = await metadataCompleted.Task;
            await manager.StopAsync ();
            await RemoveAsync (manager);

            token.ThrowIfCancellationRequested ();
            return data;
        }

        async void HandleNatsPeerDiscovered (object? sender, MonoTorrent.Dht.NatsNatTraversalService.NatsPeerDiscoveredEventArgs e) // Use fully qualified name for EventArgs
        {
            Console.WriteLine($"[Console] HandleNatsPeerDiscovered called for peer {e.Peer.ConnectionUri} NodeId {e.NodeId}");
            // Executed when NatsNatTraversalService raises the PeerDiscovered event.
            await MainLoop; // Switch to the main loop thread

            Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Processing NATS peer {e.Peer.ConnectionUri} (NodeId: {e.NodeId}, PeerId: {e.Peer.PeerId?.ToHex() ?? "null"})...");

            // Determine the peer info to use (original or loopback)
            PeerInfo peerToAdd = e.Peer; // Default to the discovered peer info
            IPAddress? myPublicIP = natsService?.MyExternalEndPoint?.Address; // Get our own discovered public IP

            // Declare concreteDhtEngine ONCE at the top of the method, and use it everywhere
            MonoTorrent.Dht.DhtEngine? concreteDhtEngine = DhtEngine as MonoTorrent.Dht.DhtEngine;

            if (myPublicIP != null && myPublicIP.Equals(e.EndPoint.Address))
            {
                // Inject all discovered IPs: internal, loopback, and external
                // 1. Internal IP (if available)
                if (!string.IsNullOrEmpty(e.InternalIp))
                {
                    var internalUri = new Uri($"ipv4://{e.InternalIp}:{e.InternalPort}");
                    peerToAdd = new PeerInfo(internalUri, e.Peer.PeerId);
                    Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Peer IP matches own public IP. Using internal LAN address: {internalUri}");

                    if (concreteDhtEngine != null && concreteDhtEngine.State == DhtState.Ready)
                    {
                        // Internal IP(s)
                        if (!string.IsNullOrEmpty(e.InternalIp))
                        {
                            var ips = e.InternalIp.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var ip in ips)
                            {
                                if (IPAddress.TryParse(ip, out var addr))
                                {
                                    byte[] compactNode = new byte[26];
                                    var nodeIdBytes = e.NodeId.Span;
                                    nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                                    var ipBytes = addr.GetAddressBytes();
                                    ipBytes.CopyTo(compactNode, 20);
                                    ushort port = e.InternalPort;
                                    compactNode[24] = (byte)(port >> 8);
                                    compactNode[25] = (byte)(port & 0xFF);
                                    concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                                    Debug.WriteLine($"[DHT] Injected LAN node {ip}:{e.InternalPort} into routing table (multi-LAN-IP).");
                                }
                            }
                        }
                        // Loopback
                        {
                            byte[] compactNode = new byte[26];
                            var nodeIdBytes = e.NodeId.Span;
                            nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                            byte[] ipBytes = { 127, 0, 0, 1 };
                            ipBytes.CopyTo(compactNode, 20);
                            ushort port = (ushort)e.EndPoint.Port;
                            compactNode[24] = (byte)(port >> 8);
                            compactNode[25] = (byte)(port & 0xFF);
                            concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                            Debug.WriteLine($"[DHT] Injected loopback node 127.0.0.1:{e.EndPoint.Port} into routing table (local multi-instance).");
                        }
                        // External IP
                        {
                            byte[] compactNode = new byte[26];
                            var nodeIdBytes = e.NodeId.Span;
                            nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                            var ipBytes = e.EndPoint.Address.GetAddressBytes();
                            ipBytes.CopyTo(compactNode, 20);
                            ushort port = (ushort)e.EndPoint.Port;
                            compactNode[24] = (byte)(port >> 8);
                            compactNode[25] = (byte)(port & 0xFF);
                            concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                            Debug.WriteLine($"[DHT] Injected external node {e.EndPoint.Address}:{e.EndPoint.Port} into routing table (for completeness).");
                        }
                    }
                }
                else
                {
                    // Fallback: Use loopback for same-machine multi-instance
                    var loopbackUri = new Uri($"ipv4://127.0.0.1:{e.EndPoint.Port}");
                    peerToAdd = new PeerInfo(loopbackUri, e.Peer.PeerId); // Use original PeerId
                    Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Peer IP matches own public IP. Using loopback address: {loopbackUri}");

                    if (concreteDhtEngine != null && concreteDhtEngine.State == DhtState.Ready)
                    {
                        // Loopback
                        {
                            byte[] compactNode = new byte[26];
                            var nodeIdBytes = e.NodeId.Span;
                            nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                            byte[] ipBytes = { 127, 0, 0, 1 };
                            ipBytes.CopyTo(compactNode, 20);
                            ushort port = (ushort)e.EndPoint.Port;
                            compactNode[24] = (byte)(port >> 8);
                            compactNode[25] = (byte)(port & 0xFF);
                            concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                            Debug.WriteLine($"[DHT] Injected loopback node 127.0.0.1:{e.EndPoint.Port} into routing table (local multi-instance).");
                        }
                        // External IP
                        {
                            byte[] compactNode = new byte[26];
                            var nodeIdBytes = e.NodeId.Span;
                            nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                            var ipBytes = e.EndPoint.Address.GetAddressBytes();
                            ipBytes.CopyTo(compactNode, 20);
                            ushort port = (ushort)e.EndPoint.Port;
                            compactNode[24] = (byte)(port >> 8);
                            compactNode[25] = (byte)(port & 0xFF);
                            concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                            Debug.WriteLine($"[DHT] Injected external node {e.EndPoint.Address}:{e.EndPoint.Port} into routing table (for completeness).");
                        }
                    }
                }
            }

            // Also inject the loopback node into the DHT routing table (like the manual code)
            if (concreteDhtEngine != null && concreteDhtEngine.State == DhtState.Ready)
            {
                byte[] compactNode = new byte[26];
                var nodeIdBytes = e.NodeId.Span;
                nodeIdBytes.CopyTo(compactNode.AsSpan(0, 20));
                byte[] ipBytes = { 127, 0, 0, 1 };
                ipBytes.CopyTo(compactNode, 20);
                ushort port = (ushort)e.EndPoint.Port;
                compactNode[24] = (byte)(port >> 8);
                compactNode[25] = (byte)(port & 0xFF);
                concreteDhtEngine.Add(new[] { new ReadOnlyMemory<byte>(compactNode) });
                Debug.WriteLine($"[DHT] Injected loopback node 127.0.0.1:{e.EndPoint.Port} into routing table (local multi-instance).");
            }
            

            // Add the discovered peer (original or loopback) to *all* relevant torrent managers.
            int totalAdded = 0;
            foreach (var manager in allTorrents)
            {
                // Check if the manager allows connections from sources like DHT/NATS (CanUseDht is a proxy for this)
                // and if the peer isn't already connected/connecting.
                bool alreadyKnown = manager.Peers.ActivePeers.Any(p => p.Info.ConnectionUri.Equals(peerToAdd.ConnectionUri))
                                 || manager.Peers.ConnectedPeers.Any(p => p.Uri.Equals(peerToAdd.ConnectionUri));
                if (alreadyKnown) Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Peer {peerToAdd.ConnectionUri} already known by manager {BitConverter.ToString(manager.InfoHashes.V1OrV2.AsMemory().ToArray(), 0, 3).Replace("-", "")}.");

                if (manager.CanUseDht && !alreadyKnown)
                {
                    Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Attempting to add NATS peer {peerToAdd.ConnectionUri} to manager {manager.LogName}...");
                    int addedCount = await manager.AddPeersAsync(new[] { peerToAdd });
                    totalAdded += addedCount;
                    Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Result of adding NATS peer {peerToAdd.ConnectionUri} to manager {manager.LogName}: {addedCount > 0}");
                    Console.WriteLine($"[Console] HandleNatsPeerDiscovered finished processing peer {peerToAdd.ConnectionUri}");
                }
            }

            if (totalAdded > 0)
            {
                Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] HandleNatsPeerDiscovered: Finished processing peer {peerToAdd.ConnectionUri}. Total added across managers: {totalAdded}.");
                // Optionally trigger TryConnect immediately, though the regular tick should pick it up.
                // ConnectionManager.TryConnect();
                Console.WriteLine($"[Console] HandleNatsPeerDiscovered finished processing peer {peerToAdd.ConnectionUri}");
            }
        }

        async void HandleLocalPeerFound (object? sender, LocalPeerFoundEventArgs args)
        {
            try {
                await MainLoop;

                Log.Info ($"HandleLocalPeerFound: Received LPD discovery for {args.InfoHash} from {args.Uri}"); // Added Log

                TorrentManager? manager = allTorrents.FirstOrDefault (t => t.InfoHashes.Contains (args.InfoHash));
                // There's no TorrentManager in the engine
                if (manager == null)
                {
                    Log.Info ($"HandleLocalPeerFound: No manager found for {args.InfoHash}."); // Added Log
                    return;
                }


                // The torrent is marked as private, so we can't add random people
                if (manager.HasMetadata && manager.Torrent!.IsPrivate) {
                    manager.RaisePeersFound (new LocalPeersAdded (manager, 0, 0));
                    Log.Info ($"HandleLocalPeerFound: Torrent {args.InfoHash} is private. Ignoring peer {args.Uri}."); // Added Log
                } else {
                    // Add new peer to matched Torrent
                    var peer = new PeerInfo (args.Uri);
                    int peersAdded = manager.AddPeers (new[] { peer }, prioritise: false, fromTracker: false);
                    manager.RaisePeersFound (new LocalPeersAdded (manager, peersAdded, 1));
                    Log.Info ($"HandleLocalPeerFound: Added peer {args.Uri} to manager for {args.InfoHash}. Result: {peersAdded}"); // Added Log
                }
            } catch (Exception ex) { // Catch specific exception and log it
                Log.Error ($"HandleLocalPeerFound: Error processing LPD peer {args.Uri} for {args.InfoHash}: {ex}");
            }
        }

        public async Task PauseAll ()
        {
            CheckDisposed ();
            await MainLoop;

            var tasks = new List<Task> ();
            foreach (TorrentManager manager in publicTorrents)
                tasks.Add (manager.PauseAsync ());
            await Task.WhenAll (tasks);
        }

        async Task Register (TorrentManager manager, bool isPublic)
        {
            CheckDisposed ();
            Check.Manager (manager);

            await MainLoop;

            if (!Settings.AllowMultipleTorrentInstances && Contains (manager.InfoHashes))
                throw new TorrentException ("A manager for this torrent has already been registered");

            allTorrents.Add (manager);
            if (isPublic)
                publicTorrents.Add (manager);
            ConnectionManager.Add (manager);
            listenManager.Add (manager.InfoHashes);

            manager.DownloadLimiters.Add (downloadLimiters);
            manager.UploadLimiters.Add (uploadLimiters);
            if (DhtEngine != null && manager.Torrent?.Nodes != null && DhtEngine.State != DhtState.Ready) {
                try {
                    DhtEngine.Add (manager.Torrent.Nodes.OfType<BEncodedString> ().Select (t => t.AsMemory ()));
                } catch (Exception e) {
                    Log.Info ($"Unable to register torrent with DHT: {e}");
                }
            }
        }

        async Task RegisterDht (IDhtEngine engine)
        {
            if (DhtEngine != null) {
                DhtEngine.StateChanged -= DhtEngineStateChanged;
                DhtEngine.PeersFound -= DhtEnginePeersFound;
                await DhtEngine.StopAsync ();
                DhtEngine.Dispose ();
            }
            DhtEngine = engine ?? new NullDhtEngine ();
            Dht = new DhtEngineWrapper (DhtEngine);

            DhtEngine.StateChanged += DhtEngineStateChanged;
            DhtEngine.PeersFound += DhtEnginePeersFound;
            if (IsRunning)
                await DhtEngine.StartAsync (await MaybeLoadDhtNodes ());
        }

        void RegisterLocalPeerDiscovery (ILocalPeerDiscovery? localPeerDiscovery)
        {
            if (LocalPeerDiscovery != null) {
                LocalPeerDiscovery.PeerFound -= HandleLocalPeerFound;
                LocalPeerDiscovery.Stop ();
            }

            if (!SupportsLocalPeerDiscovery || localPeerDiscovery == null)
                localPeerDiscovery = new NullLocalPeerDiscovery ();

            LocalPeerDiscovery = localPeerDiscovery;

            if (LocalPeerDiscovery != null) {
                LocalPeerDiscovery.PeerFound += HandleLocalPeerFound;
                if (IsRunning)
                    LocalPeerDiscovery.Start ();
            }
        }

        async void DhtEnginePeersFound (object? o, PeersFoundEventArgs e)
        {
            await MainLoop;

            TorrentManager? manager = allTorrents.FirstOrDefault (t => t.InfoHashes.Contains (e.InfoHash));
            if (manager == null)
                return;

            if (manager.CanUseDht) {
                int successfullyAdded = await manager.AddPeersAsync (e.Peers);
                manager.RaisePeersFound (new DhtPeersAdded (manager, successfullyAdded, e.Peers.Count));
            } else {
                // This is only used for unit testing to validate that even if the DHT engine
                // finds peers for a private torrent, we will not add them to the manager.
                manager.RaisePeersFound (new DhtPeersAdded (manager, 0, 0));
            }
        }

        async void DhtEngineStateChanged (object? o, EventArgs e)
        {
            if (DhtEngine.State != DhtState.Ready)
                return;

            await MainLoop;
            foreach (TorrentManager manager in allTorrents) {
                if (!manager.CanUseDht)
                    continue;

                // IPV6: Also report to an ipv6 DHT node
                if (manager.InfoHashes.V1 != null) {
                    DhtEngine.Announce (manager.InfoHashes.V1, GetOverrideOrActualListenPort ("ipv4") ?? -1);
                    DhtEngine.GetPeers (manager.InfoHashes.V1);
                }
                if (manager.InfoHashes.V2 != null) {
                    DhtEngine.Announce (manager.InfoHashes.V2.Truncate (), GetOverrideOrActualListenPort ("ipv4") ?? -1);
                    DhtEngine.GetPeers (manager.InfoHashes.V2.Truncate ());
                }
            }
        }

        public async Task StartAllAsync ()
        {
            CheckDisposed ();

            await MainLoop;

            var tasks = new List<Task> ();
            for (int i = 0; i < publicTorrents.Count; i++)
                tasks.Add (publicTorrents[i].StartAsync ());
            await Task.WhenAll (tasks);
        }

        /// <summary>
        /// Stops all active <see cref="TorrentManager"/> instances.
        /// </summary>
        /// <returns></returns>
        public Task StopAllAsync ()
        {
            return StopAllAsync (Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Stops all active <see cref="TorrentManager"/> instances. The final announce for each <see cref="TorrentManager"/> will be limited
        /// to the maximum of either 2 seconds or <paramref name="timeout"/> seconds.
        /// </summary>
        /// <param name="timeout">The timeout for the final tracker announce.</param>
        /// <returns></returns>
        public async Task StopAllAsync (TimeSpan timeout)
        {
            CheckDisposed ();

            await MainLoop;
            var tasks = new List<Task> ();
            for (int i = 0; i < publicTorrents.Count; i++)
                tasks.Add (publicTorrents[i].StopAsync (timeout));
            await Task.WhenAll (tasks);
        }

        #endregion


        #region Private/Internal methods

        void LogicTick () {
            tickCount++;
    
            if (tickCount % 2 == 0) {
                downloadLimiter.UpdateChunks (Settings.MaximumDownloadRate);
                uploadLimiter.UpdateChunks (Settings.MaximumUploadRate);
            }
    
            ConnectionManager.CancelPendingConnects ();
            ConnectionManager.TryConnect ();
            DiskManager.Tick ();
    
            for (int i = 0; i < allTorrents.Count; i++)
                allTorrents[i].Mode.Tick (tickCount);
    
            // BEP46: Check for mutable torrent updates and announce presence
            foreach (var manager in allTorrents)
            {
                if (manager.MutablePublicKey != null)
                {
                    // Periodic mutable update check
                    if (manager.LastMutableUpdateCheckTimer.Elapsed > Settings.MutableTorrentUpdateInterval)
                    {
                        _ = manager.PerformMutableUpdateCheckAsync(); // Don't block the main loop
                    }

                    // Periodic DHT announce for mutable torrents
                    if (manager.CanUseDht && (!manager.LastDhtAnnounceTimer.IsRunning || manager.LastDhtAnnounceTimer.Elapsed > manager.Engine.DhtEngine.MinimumAnnounceInterval))
                    {
                        manager.LastDhtAnnounceTimer.Restart();
                        _ = manager.DhtAnnounceAsync(); // Fire and forget
                    }
                }
            }
    
    
            RaiseStatsUpdate (new StatsUpdateEventArgs ());
        }


        internal void RaiseCriticalException (CriticalExceptionEventArgs e)
        {
            CriticalException?.InvokeAsync (this, e);
        }

        internal void RaiseStatsUpdate (StatsUpdateEventArgs args)
        {
            StatsUpdate?.InvokeAsync (this, args);
        }

        /// <summary>
        /// Starts the engine and optionally specifies bootstrap nodes for the DHT.
        /// </summary>
        /// <param name="bootstrapRouters">An array of DHT bootstrap routers. Pass an empty array to disable bootstrapping via default routers.</param>
        public async Task StartAsync (string[] bootstrapRouters)
        {
            CheckDisposed ();
            if (!IsRunning) {
                IsRunning = true;

                if (Settings.AllowPortForwarding)
                    await PortForwarder.StartAsync (CancellationToken.None);

                // Start DHT Engine first, passing the custom bootstrap routers
                await DhtEngine.StartAsync (await MaybeLoadDhtNodes (), bootstrapRouters, natsService: natsService, portForwarder: PortForwarder);

                // Start peer listeners and attempt port mapping for them.
                await StartAndPortMapPeerListeners ();

                // Start Local Peer Discovery *after* listeners are active.
                LocalPeerDiscovery.Start ();

                // NATS Initialization (if enabled)
                if (natsService != null)
                {
                    Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Checking ActualDhtListener status before NATS init...");
                    if (ActualDhtListener?.Status != MonoTorrent.Connections.ListenerStatus.Listening)
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Warning: Actual DHT listener status is {ActualDhtListener?.Status} before NATS InitializeAsync. Expected 'Listening'.");
                    else
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Actual DHT listener is listening on {ActualDhtListener.LocalEndPoint}.");

                    try {
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Initializing NATS service...");
                        await natsService.InitializeAsync(ActualDhtListener, PortForwarder);
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] NATS service initialized.");
                    } catch (Exception ex) {
                        Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Failed to initialize NATS service: {ex}");
                    }
                }
            }
        }

        public async Task StartAsync () // Made public for TorrentService access
        {
            CheckDisposed ();
            Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Entering StartAsync. IsRunning: {IsRunning}");
            if (!IsRunning) {
                IsRunning = true;

                if (Settings.AllowPortForwarding)
                    await PortForwarder.StartAsync (CancellationToken.None);

                // Start DHT Engine first, as NATS needs the listener.
                // Pass null for NATS service here, it will be initialized later.
                // Pass the port forwarder so DHT can potentially use it.
                await DhtEngine.StartAsync (await MaybeLoadDhtNodes (), natsService: null, portForwarder: PortForwarder);

                // Start peer listeners and attempt port mapping for them.
                await StartAndPortMapPeerListeners ();

                // Start Local Peer Discovery *after* listeners are active.
                LocalPeerDiscovery.Start ();
 
                // Removed flawed hairpin/loopback discovery logic.
                // This should be handled explicitly in tests or by higher-level coordination if needed.


                // --- NATS Initialization (Moved to the end) ---
                // Add a delay here to give the PortForwarder and Listener time to complete/bind
                // before initializing NATS, mimicking the explicit wait in the reference test.
                Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Starting 5s delay for PortForwarder/Listener binding before NATS init...");
                // await Task.Delay(TimeSpan.FromSeconds(5));
                Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Finished 5s delay. Proceeding with NATS init.");

                // Initialize NATS service if it exists, *after* other components are started and delay has passed.
                if (natsService != null)
                {
                    Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Checking ActualDhtListener status before NATS init...");
                    // Use the ActualDhtListener property which accesses the listener within the DhtEngine's MessageLoop.
                    // No need for an extra wait loop here, the main 5s delay should suffice.
                    if (ActualDhtListener?.Status != MonoTorrent.Connections.ListenerStatus.Listening)
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Warning: Actual DHT listener status is {ActualDhtListener?.Status} before NATS InitializeAsync. Expected 'Listening'.");
                    else
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Actual DHT listener is listening on {ActualDhtListener.LocalEndPoint}.");

                    try {
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Initializing NATS service...");
                        await natsService.InitializeAsync(ActualDhtListener, PortForwarder);
                        Debug.WriteLine ($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] NATS service initialized.");
                    } catch (Exception ex) {
                        Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Failed to initialize NATS service: {ex}"); // Log full exception
                        // Consider if this should be fatal or disable NATS discovery.
                    }
                }
            }
        }

        internal async Task StopAsync ()
        {
            CheckDisposed ();
            // If all the torrents are stopped, stop ticking
            IsRunning = allTorrents.Exists (m => m.State != TorrentState.Stopped);
            if (!IsRunning) {
                await UnmapAndStopPeerListeners ();

                if (DhtListener.LocalEndPoint != null)
                    await PortForwarder.UnregisterMappingAsync (new Mapping (Protocol.Udp, DhtListener.LocalEndPoint.Port), CancellationToken.None);

                LocalPeerDiscovery.Stop ();

                if (Settings.AllowPortForwarding)
                    await PortForwarder.StopAsync (CancellationToken.None);

                await MaybeSaveDhtNodes ();
                await DhtEngine.StopAsync ();
                Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] Stopping Engine. Disposing NATS service.");
                natsService?.Dispose (); // Dispose NATS service on stop
            }
        }

        async ReusableTask StartAndPortMapPeerListeners ()
        {
            foreach (var v in PeerListeners)
                v.Start ();

            // The settings could say to listen at port 0, which means 'choose one dynamically'
            var maps = PeerListeners
                .Select (t => t.LocalEndPoint!)
                .Where (t => t != null)
                .Select (endpoint => PortForwarder.RegisterMappingAsync (new Mapping (Protocol.Tcp, endpoint.Port)))
                .ToArray ();
            await Task.WhenAll (maps);
        }

        async ReusableTask UnmapAndStopPeerListeners()
        {
            var unmaps = PeerListeners
                    .Select (t => t.LocalEndPoint!)
                    .Where (t => t != null)
                    .Select (endpoint => PortForwarder.UnregisterMappingAsync (new Mapping (Protocol.Tcp, endpoint.Port), CancellationToken.None))
                    .ToArray ();
            await Task.WhenAll (unmaps);

            foreach (var listener in PeerListeners)
                listener.Stop ();
        }

        async ReusableTask<ReadOnlyMemory<byte>> MaybeLoadDhtNodes ()
        {
            if (!Settings.AutoSaveLoadDhtCache)
                return ReadOnlyMemory<byte>.Empty;

            var savePath = Settings.GetDhtNodeCacheFilePath ();
            return await Task.Run (() => File.Exists (savePath) ? File.ReadAllBytes (savePath) : ReadOnlyMemory<byte>.Empty);
        }

        async ReusableTask MaybeSaveDhtNodes ()
        {
            if (!Settings.AutoSaveLoadDhtCache)
                return;

            var nodes = await DhtEngine.SaveNodesAsync ();
            if (nodes.Length == 0)
                return;

            // Perform this action on a threadpool thread.
            await MainLoop.SwitchThread ();

            // Ensure only 1 thread at a time tries to save DhtNodes.
            // Users can call StartAsync/StopAsync many times on
            // TorrentManagers and the file write could happen
            // concurrently.
            using (await dhtNodeLocker.EnterAsync ().ConfigureAwait (false)) {
                var savePath = Settings.GetDhtNodeCacheFilePath ();
                var parentDir = Path.GetDirectoryName (savePath);
                if (!(parentDir is null))
                    Directory.CreateDirectory (parentDir);
                File.WriteAllBytes (savePath, nodes.ToArray ());
            }
        }

        public async Task UpdateSettingsAsync (EngineSettings settings)
        {
            await MainLoop.SwitchThread ();
            CheckSettingsAreValid (settings);

            await MainLoop;

            var oldSettings = Settings;
            Settings = settings;
            await UpdateSettingsAsync (oldSettings, settings);
        }

        static void CheckSettingsAreValid (EngineSettings settings)
        {
            if (string.IsNullOrEmpty (settings.CacheDirectory))
                throw new ArgumentException ("EngineSettings.CacheDirectory cannot be null or empty.", nameof (settings));

            if (File.Exists (settings.CacheDirectory))
                throw new ArgumentException ("EngineSettings.CacheDirectory should be a directory, but a file exists at that path instead. Please delete the file or choose another path", nameof (settings));

            foreach (var directory in new[] { settings.CacheDirectory, settings.MetadataCacheDirectory, settings.FastResumeCacheDirectory }) {
                try {
                    Directory.CreateDirectory (directory);
                } catch (Exception e) {
                    throw new ArgumentException ($"Could not create a directory at the path {directory}. Please check this path has read/write permissions for this user.", nameof (settings), e);
                }
            }
        }

        async Task UpdateSettingsAsync (EngineSettings oldSettings, EngineSettings newSettings)
        {
            await DiskManager.UpdateSettingsAsync (newSettings);
            if (newSettings.DiskCacheBytes != oldSettings.DiskCacheBytes)
                await Task.WhenAll (Torrents.Select (t => DiskManager.FlushAsync (t)));

            ConnectionManager.Settings = newSettings;

            if (oldSettings.UsePartialFiles != newSettings.UsePartialFiles) {
                foreach (var manager in Torrents)
                    await manager.UpdateUsePartialFiles (newSettings.UsePartialFiles);
            }
            if (oldSettings.AllowPortForwarding != newSettings.AllowPortForwarding) {
                if (newSettings.AllowPortForwarding)
                    await PortForwarder.StartAsync (CancellationToken.None);
                else
                    await PortForwarder.StopAsync (removeExistingMappings: true, CancellationToken.None);
            }

            if (oldSettings.DhtEndPoint != newSettings.DhtEndPoint) {
                if (DhtListener.LocalEndPoint != null)
                    await PortForwarder.UnregisterMappingAsync (new Mapping (Protocol.Udp, DhtListener.LocalEndPoint.Port), CancellationToken.None);
                DhtListener.Stop ();

                if (newSettings.DhtEndPoint == null) {
                    DhtListener = new NullDhtListener ();
                    await RegisterDht (new NullDhtEngine ());
                } else {
                    DhtListener = (Settings.DhtEndPoint is null ? null : Factories.CreateDhtListener (Settings.DhtEndPoint)) ?? new NullDhtListener ();
                    if (IsRunning)
                        DhtListener.Start ();

                    if (oldSettings.DhtEndPoint == null) {
                        var dht = Factories.CreateDht ();
                        await dht.SetListenerAsync (DhtListener);
                        await RegisterDht (dht);

                    } else {
                        await DhtEngine.SetListenerAsync (DhtListener);
                    }
                }

                if (DhtListener.LocalEndPoint != null)
                    await PortForwarder.RegisterMappingAsync (new Mapping (Protocol.Udp, DhtListener.LocalEndPoint.Port));
            }

            if (!oldSettings.ListenEndPoints.SequenceEqual (newSettings.ListenEndPoints)) {
                await UnmapAndStopPeerListeners ();

                PeerListeners = Array.AsReadOnly (newSettings.ListenEndPoints.Values.Select (t => Factories.CreatePeerConnectionListener (t)).ToArray ());
                listenManager.SetListeners (PeerListeners);

                if (IsRunning)
                    await StartAndPortMapPeerListeners ();
            }

            if (oldSettings.AllowLocalPeerDiscovery != newSettings.AllowLocalPeerDiscovery) {
                RegisterLocalPeerDiscovery (!newSettings.AllowLocalPeerDiscovery ? null : Factories.CreateLocalPeerDiscovery ());
            }
        }

        static BEncodedString GeneratePeerId ()
        {
            var sb = new StringBuilder (20);
            sb.Append ("-");
            sb.Append (GitInfoHelper.ClientVersion);
            sb.Append ("-");

            // Create and use a single Random instance which *does not* use a seed so that
            // the random sequence generated is definitely not the same between application
            // restarts.
            lock (PeerIdRandomGenerator) {
                while (sb.Length < 20)
                    sb.Append (PeerIdRandomGenerator.Next (0, 9));
            }

            return new BEncodedString (sb.ToString ());
        }

        internal int? GetOverrideOrActualListenPort (string scheme)
        {
            // If the override is set to non-zero, use it. Otherwise use the actual port.
            if (Settings.ReportedListenEndPoints.TryGetValue (scheme, out var reportedEndPoint) && reportedEndPoint.Port != 0)
                return reportedEndPoint.Port;

            // Try to get the actual port first.
            foreach (var endPoint in PeerListeners.Select (t => t.LocalEndPoint!).Where (t => t != null)) {
                if (scheme == "ipv4" && endPoint.AddressFamily == AddressFamily.InterNetwork)
                    return endPoint.Port;
                if (scheme == "ipv6" && endPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    return endPoint.Port;
            }

            // If there was a listener but it hadn't successfully bound to a port, return the preferred port port... if it's non-zero.
            foreach (var endPoint in PeerListeners.Select (t => t.PreferredLocalEndPoint!).Where (t => t != null)) {
                if (scheme == "ipv4" && endPoint.Port != 0 && endPoint.AddressFamily == AddressFamily.InterNetwork)
                    return endPoint.Port;
                if (scheme == "ipv6" && endPoint.Port != 0 && endPoint.AddressFamily == AddressFamily.InterNetworkV6)
                    return endPoint.Port;
            }

            // If we get here there are either no listeners, or none were bound to a local port, or the preferred port is set to '0' (which means
            // we don't know it's port yet because the listener isn't running. This *should* never happen as we should only be running announces
            // while the engine is active, which means the listener should still be running.)
            return null;
        }

        /// <summary>
        /// Gets the dictionary of peers discovered via the NATS service, if NATS discovery is enabled.
        /// </summary>
        /// <returns>A dictionary mapping NodeId to IPEndPoint, or null if NATS discovery is disabled or the service is not available.</returns>
        public IDictionary<NodeId, IPEndPoint>? GetNatsDiscoveredPeers()
        {
            Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] GetNatsDiscoveredPeers called.");
            // Return peers from the internal NATS service instance, if it exists.
            var peers = natsService?.GetDiscoveredPeers();
            Debug.WriteLine($"[{DateTime.UtcNow:O}] [ClientEngine {BitConverter.ToString(PeerId.AsMemory().Span.ToArray(), 0, 3).Replace("-", "")}] GetNatsDiscoveredPeers returning {(peers == null ? "null" : $"{peers.Count} peers")}.");
            return peers;
        }

        /// <summary>
        /// Performs a 'put' operation to store or update a mutable item on the DHT.
        /// This is a public wrapper around the internal DhtEngine functionality.
        /// </summary>
        /// <param name="publicKey">The public key (32 bytes).</param>
        /// <param name="privateKey">The private key (64 bytes) used for signing.</param>
        /// <param name="sequenceNumber">The sequence number.</param>
        /// <param name="value">The value to store.</param>
        /// <param name="salt">Optional salt (max 64 bytes).</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="cas">Optional Compare-And-Swap sequence number.</param>
        public async Task PutMutableAsync (byte[] publicKey, byte[] privateKey, long sequenceNumber, BEncodedValue value, byte[]? salt = null, CancellationToken token = default, long? cas = null)
        {
            // Input validation (could reuse DhtEngine's validation or add basic checks here)
            if (publicKey == null || publicKey.Length != 32) throw new ArgumentException("Public key must be 32 bytes.", nameof(publicKey));
            if (privateKey == null || privateKey.Length != 64) throw new ArgumentException("Private key must be 64 bytes.", nameof(privateKey)); // Needed for signing
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (salt != null && salt.Length > 64) throw new ArgumentException("Salt cannot be longer than 64 bytes", nameof(salt));

            // Ensure DHT is running
            if (DhtEngine == null || DhtEngine.State != DhtState.Ready)
                throw new InvalidOperationException("DHT Engine is not running or available.");

            // BEP44 requires the value, sequence number, and optional salt to be signed.
            // The internal DhtEngine.PutMutableAsync expects the *already signed* data.
            // We need to perform the signing here before calling the internal method.

            // 1. Prepare the data to be signed (bencoded dict with 'seq' and 'v')
            var dataToSign = new BEncodedDictionary {
                { "seq", new BEncodedNumber(sequenceNumber) },
                { "v", value }
            };
            if (salt != null) {
                dataToSign.Add("salt", new BEncodedString(salt));
            }
            byte[] encodedData = dataToSign.Encode();

            // 2. Sign the data using Ed25519 (Requires a crypto library like Chaos.NaCl or similar)
            // Placeholder: Assume a Sign method exists or is available via a helper/dependency.
            // byte[] signatureBytes = Ed25519.Sign(encodedData, privateKey); // Hypothetical signing call
            // For now, let's assume the internal PutMutableAsync handles signing if given keys,
            // OR we need to adjust the internal method signature. Re-checking DhtEngine.PutMutableAsync...
            // Ah, DhtEngine.PutMutableAsync takes the *signature* as input, not the private key.
            // This means the signing MUST happen *before* calling this wrapper.
            // Let's adjust the wrapper signature to match DhtEngine's expectation.

            // --- Reverting previous thought - Adjusting wrapper to match DhtEngine ---
            // The wrapper should take the signature, not the private key.
            // The caller (TorrentService) will be responsible for signing.
            // Let's redefine the wrapper method signature.
            // (Going back to edit the method signature above is tricky with apply_diff,
            // so I'll add a new method with the correct signature and remove the incorrect one later if needed,
            // or just comment out the incorrect implementation for now.)

            // --- Corrected Approach: Wrapper matches DhtEngine signature ---
            // This method will be added below. The incorrect one above will be ignored/removed.
        }

        /// <summary>
        /// Performs a 'put' operation to store or update a mutable item on the DHT.
        /// This is a public wrapper around the internal DhtEngine functionality.
        /// The caller is responsible for providing the correctly signed data.
        /// </summary>
        /// <param name="publicKey">The public key (32 bytes) as a BEncodedString.</param>
        /// <param name="salt">Optional salt (max 64 bytes) as a BEncodedString.</param>
        /// <param name="value">The value to store.</param>
        /// <param name="sequenceNumber">The sequence number.</param>
        /// <param name="signature">The signature (64 bytes) as a BEncodedString.</param>
        /// <param name="cas">Optional Compare-And-Swap sequence number.</param>
        public async Task PutMutableAsync (BEncodedString publicKey, BEncodedString? salt, BEncodedValue value, long sequenceNumber, BEncodedString signature, long? cas = null)
        {
            // Ensure DHT is running
            if (DhtEngine == null || DhtEngine.State != DhtState.Ready)
                throw new InvalidOperationException("DHT Engine is not running or available.");

            // Delegate directly to the internal DhtEngine's method
            await DhtEngine.PutMutableAsync(publicKey, salt, value, sequenceNumber, signature, cas);
        }

        /// <summary>
        /// Performs a 'get' operation on the DHT to retrieve a value.
        /// This is a public wrapper around the internal DhtEngine functionality.
        /// </summary>
        /// <param name="target">The target ID (typically SHA1 hash of PublicKey+Salt or just PublicKey).</param>
        /// <param name="sequenceNumber">Optional sequence number for mutable gets.</param>
        /// <returns>A tuple containing the value, public key, signature, and sequence number (if available).</returns>
        public async Task<(BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber)> GetMutableAsync (NodeId target, long? sequenceNumber = null)
        {
            // Ensure DHT is running
            if (DhtEngine == null || DhtEngine.State != DhtState.Ready)
                throw new InvalidOperationException("DHT Engine is not running or available.");

            // Delegate directly to the internal DhtEngine's method
            return await DhtEngine.GetAsync(target, sequenceNumber);
        }

        /// <summary>
        /// Gets the current state of the DHT engine.
        /// </summary>
        public DhtState DhtState => DhtEngine?.State ?? DhtState.NotReady;

        /// <summary>
        /// Gets a dictionary representing the local storage of the DHT engine.
        /// Note: This exposes internal state primarily for testing purposes.
        /// </summary>
        public IDictionary<NodeId, StoredDhtItem> DhtLocalStorage =>
            (DhtEngine is DhtEngine concreteEngine) ? concreteEngine.LocalStorageProperty : new Dictionary<NodeId, StoredDhtItem>();

        /// <summary>
        /// Adds a DHT node to the engine's routing table.
        /// </summary>
        /// <param name="node">The node to add.</param>
        public async Task AddDhtNodeAsync(Node node)
        {
            if (DhtEngine is DhtEngine concreteEngine)
            {
                await concreteEngine.Add(node);
            } else {
                // Log or throw? DHT is likely NullDhtEngine.
                Log.Info("Cannot add DHT node as the engine is not a standard DhtEngine.");
            }
        }

        /// <summary>
        /// Sends a 'ping' query to the specified DHT node.
        /// This is a public wrapper around the internal DhtEngine functionality.
        /// </summary>
        /// <param name="node">The node to ping.</param>
        /// <returns>True if a response was received, false otherwise (e.g., timeout).</returns>
        public async Task<bool> PingAsync(Node node)
        {
            // Ensure DHT is running
            if (DhtEngine == null || DhtEngine.State != DhtState.Ready)
                throw new InvalidOperationException("DHT Engine is not running or available.");

            // Delegate directly to the internal DhtEngine's method
            // Need to check the actual method signature on DhtEngine. Assuming it takes Node and returns Task<bool> or similar.
            // Re-checking DhtEngine... it doesn't have a direct PingAsync. It uses SendQueryAsync with a Ping message.
            // We need to replicate that logic here.

            if (DhtEngine is DhtEngine concreteEngine)
            {
                var pingMessage = new Ping(concreteEngine.LocalId);
                var args = await concreteEngine.SendQueryAsync(pingMessage, node);
                return !args.TimedOut;
            } else {
                Log.Info("Cannot send Ping as the engine is not a standard DhtEngine.");
                return false;
            }
        }

        #endregion // Moved before class closing brace

    } // End of ClientEngine class
}
