//
// TorrentManager.cs
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


using System.Buffers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Client.Modes;
using MonoTorrent.Client.RateLimiters;
using MonoTorrent.Dht; // Added for NodeId
using MonoTorrent.Logging;
using MonoTorrent.Messages.Peer;
using MonoTorrent.PiecePicking;
using MonoTorrent.PieceWriter;
using MonoTorrent.Streaming;
using MonoTorrent.Trackers;
using Org.BouncyCastle.Crypto.Parameters; // Replacement for NSec Ed25519
using Org.BouncyCastle.Crypto.Signers;   // Replacement for NSec Ed25519
using System.Security.Cryptography; // Added for SHA1

using ReusableTasks;

namespace MonoTorrent.Client
{
    public class TorrentManager : ITorrentManagerInfo, IPieceRequesterData, IMessageEnqueuer, IPeerExchangeSource, IPieceHashStatusUpdater, IPieceHashesProvider, IRaiseInterest
    {
        static readonly Logger logger = Logger.Create (nameof (TorrentManager));

        #region Events

        internal event EventHandler<ReadOnlyMemory<byte>>? MetadataReceived;

        /// <summary>
        /// This asynchronous event is raised whenever a new incoming, or outgoing, connection
        /// has successfully completed the handshake process and has been fully established.
        /// <summary>
        /// Raised when a connection to a peer is successfully established after the handshake.
        /// </summary>
        /// </summary>
        public event EventHandler<PeerConnectedEventArgs>? PeerConnected;

        /// <summary>
        /// This asynchronous event is raised whenever an established connection has been
        /// closed.
        /// <summary>
        /// Raised when a connection to a peer is closed.
        /// </summary>
        /// </summary>
        public event EventHandler<PeerDisconnectedEventArgs>? PeerDisconnected;

        /// <summary>
        /// This asynchronous event is raised when an outgoing connection to a peer
        /// could not be established.
        /// </summary>
        public event EventHandler<ConnectionAttemptFailedEventArgs>? ConnectionAttemptFailed;
        /// <summary>
        /// Raised when an attempt to connect to a peer fails.
        /// </summary>

        /// <summary>
        /// This event is raised synchronously and is only used supposed to be used by tests.
        /// </summary>
        internal event Action<IMode, IMode>? ModeChanged;

        /// <summary>
        /// Raised whenever new peers are discovered and added. The object will be of type
        /// <see cref="TrackerPeersAdded"/>, <see cref="PeerExchangePeersAdded"/>, <see cref="LocalPeersAdded"/>
        /// or <see cref="DhtPeersAdded"/> depending on the source of the new peers.
        /// <summary>
        /// Raised when peers are discovered via any mechanism (Tracker, DHT, PEX, LSD).
        /// </summary>
        /// </summary>
        public event EventHandler<PeersAddedEventArgs>? PeersFound;
        /// <summary>
        /// Sets the download priority for a specific file within the torrent.
        /// </summary>
        /// <param name="file">The file whose priority should be changed.</param>
        /// <param name="priority">The new priority level.</param>
        /// <returns>A task representing the asynchronous operation.</returns>

        public async Task SetFilePriorityAsync (ITorrentManagerFile file, Priority priority)
        {
            if (!Files.Contains (file))
                throw new ArgumentNullException (nameof (file), "The file is not part of this torrent");

            // No change - bail out
            if (priority == file.Priority)
                return;

            await ClientEngine.MainLoop;

            if (Engine == null)
                throw new InvalidOperationException ("This torrent manager has been removed from it's ClientEngine");

            // If the old priority, or new priority, is 'DoNotDownload' then the selector needs to be refreshed
            bool needsToUpdateSelector = file.Priority == Priority.DoNotDownload || priority == Priority.DoNotDownload;
            var oldPriority = file.Priority;

            if (oldPriority == Priority.DoNotDownload && !(await Engine.DiskManager.CheckFileExistsAsync (file))) {
                // Always create the file the user requested to download
                await Engine.DiskManager.CreateAsync (file, Engine.Settings.FileCreationOptions);

                if (file.Length == 0)
                    ((TorrentFileInfo) file).BitField[0] = await Engine.DiskManager.CheckFileExistsAsync (file);
            }

            // Update the priority for the file itself now that we've successfully created it!
            ((TorrentFileInfo) file).Priority = priority;

            if (oldPriority == Priority.DoNotDownload && file.Length > 0) {
                // Look for any file which are still marked DoNotDownload but also overlap this file.
                // We need to create those ones too because if there are three 400kB files and the
                // piece length is 512kB, and the first file is set to 'DoNotDownload', then we still
                // need to create it as we'll download the first 512kB under bittorrent v1.
                foreach (var maybeCreateFile in Files.Where (t => t.Priority == Priority.DoNotDownload && t.Length > 0)) {
                    // If this file overlaps, create it!
                    if (maybeCreateFile.Overlaps(file) && !(await Engine.DiskManager.CheckFileExistsAsync (maybeCreateFile)))
                        await Engine.DiskManager.CreateAsync (maybeCreateFile, Engine.Settings.FileCreationOptions);
                }
            }
;

            // With the new priority, calculate which files we're actively downloading!
            if (needsToUpdateSelector) {
                // If we change the priority of a file we need to figure out which files are marked
                // as 'DoNotDownload' and which ones are downloadable.
                PartialProgressSelector.SetAll (false);
                if (Files.All (t => t.Priority != Priority.DoNotDownload)) {
                    PartialProgressSelector.SetAll (true);
                } else {
                    PartialProgressSelector.SetAll (false);
                    foreach (var f in Files.Where (t => t.Priority != Priority.DoNotDownload))
                        PartialProgressSelector.SetTrue ((f.StartPieceIndex, f.EndPieceIndex));
                }
            }

            Mode.HandleFilePriorityChanged (file, oldPriority);

            if (priority > oldPriority)
                Mode.RaiseInterest ();
        }

        public async Task RaiseInterest()
        {
            await ClientEngine.MainLoop;

            var affected = Mode.RaiseInterest ();
            foreach (var peer in Peers.ConnectedPeers) {
                if (peer.AmInterested)
                    Engine!.ConnectionManager.TryProcessQueue (this, peer);
            }
        }

        /// <summary>
        /// This asynchronous event is raised whenever a piece is hashed, either as part of
        /// regular downloading, or as part of a <see cref="HashCheckAsync(bool)"/>.
        /// <summary>
        /// Raised whenever a piece is successfully hashed and verified against the torrent metadata.
        /// </summary>
        /// </summary>
        public event EventHandler<PieceHashedEventArgs>? PieceHashed;

        /// <summary>
        /// This asynchronous event is raised whenever the TorrentManager changes state.
        /// <summary>
        /// Raised whenever the state of the TorrentManager changes (e.g., from Downloading to Seeding).
        /// </summary>
        /// </summary>
        public event EventHandler<TorrentStateChangedEventArgs>? TorrentStateChanged;

        internal event EventHandler<PeerAddedEventArgs>? OnPeerFound;

        #endregion

        /// <summary>
        /// Raised when a BEP46 mutable torrent has received an update notification.
        /// The application should typically stop the current TorrentManager and start
        /// a new one using the provided InfoHash.
        /// <summary>
        /// Raised when an update is found for a BEP46 mutable torrent via DHT 'get' requests.
        /// The application should typically stop this TorrentManager and start a new one with the provided InfoHash.
        /// </summary>
        /// </summary>
        public event EventHandler<TorrentUpdateEventArgs>? TorrentUpdateAvailable;



        #region Member Variables

        internal Queue<int> finishedPieces;     // The list of pieces which we should send "have" messages for
        IMode mode;
        internal DateTime lastCalledInactivePeerManager = DateTime.Now;
        TaskCompletionSource<Torrent> MetadataTask { get; }

        internal BEncodedString? MutablePublicKey { get; private set; }
        internal BEncodedString? MutableSalt { get; private set; }
        internal long? LastKnownSequenceNumber { get; private set; }
        internal ValueStopwatch LastMutableUpdateCheckTimer;

        #endregion Member Variables


        #region Properties

        public ReadOnlyBitField Bitfield => MutableBitField;

        private BitField MutableBitField { get; set; }

        public bool CanUseDht => Settings.AllowDht && (Torrent == null || !Torrent.IsPrivate);

        public bool CanUseLocalPeerDiscovery => ClientEngine.SupportsLocalPeerDiscovery && (Torrent == null || !Torrent.IsPrivate) && Engine != null;
        /// <summary>
        /// Returns true only when all files (excluding those marked DoNotDownload) have been fully downloaded and verified.
        /// </summary>

        /// <summary>
        /// Returns true only when all files have been fully downloaded, all zero-length files exist, and
        /// all files have the correct length. If some files are marked as 'DoNotDownload' then the
        /// torrent will not be considered to be Complete until they are downloaded.
        /// </summary>
        public bool Complete => Bitfield.AllTrue && AllFilesCorrectLength;

        internal bool Disposed { get; private set; }

        RateLimiter DownloadLimiter { get; }

        internal RateLimiterGroup DownloadLimiters { get; }

        public ClientEngine? Engine { get; }

        public Error? Error { get; private set; }

        public IList<ITorrentManagerFile> Files { get; private set; }

        internal IMode Mode {
            get => mode;
            set {
                IMode oldMode = mode;
                mode = value;
                ModeChanged?.Invoke (oldMode, mode);
                if (oldMode != null)
                    RaiseTorrentStateChanged (new TorrentStateChangedEventArgs (this, oldMode.State, mode.State));
                oldMode?.Dispose ();
                mode.Tick (0);
            }
        }

        internal void RaiseMetadataReceived (ReadOnlyMemory<byte> metadata)
        {
            MetadataReceived?.Invoke (this, metadata);
        }

        /// <summary>
        /// Marks the <see cref="TorrentManager"/> as needing a full hash check. If <see cref="EngineSettings.AutoSaveLoadFastResume"/>
        /// is enabled this method will also delete fast resume data from the location specified by
        /// <see cref="EngineSettings.GetFastResumePath(InfoHashes)"/>. This can only be invoked when the <see cref="State"/> is
        /// <see cref="TorrentState.Stopped"/>.
        /// </summary>
        /// <returns></returns>
        public async Task SetNeedsHashCheckAsync ()
        {
            await ClientEngine.MainLoop;
            if (State != TorrentState.Stopped)
                throw new InvalidOperationException ("SetNeedsHashCheckAsync can only be called when the TorrentManager is in the 'Stopped' state");
            SetNeedsHashCheck ();
        }

        internal void SetNeedsHashCheck ()
        {
            HashChecked = false;
            AllFilesCorrectLength = false;
            if (Engine != null && Engine.Settings.AutoSaveLoadFastResume) {
                var path = Engine.Settings.GetFastResumePath (InfoHashes);
                if (File.Exists (path))
                    File.Delete (path);
            }
        }

        /// <summary>
        /// If <see cref="ITorrentManagerFile.Priority"/> is set to <see cref="Priority.DoNotDownload"/> then the pieces
        /// associated with that <see cref="TorrentFile"/> will not be hash checked. An IgnoringPicker is used
        /// to ensure pieces which have not been hash checked are never downloaded.
        /// </summary>
        internal BitField UnhashedPieces { get; set; }

        public bool HashChecked { get; private set; }

        internal bool AllFilesCorrectLength { get; private set; }

        /// <summary>
        /// The number of times a piece is downloaded, but is corrupt and fails the hashcheck and must be re-downloaded.
        /// </summary>
        public int HashFails { get; internal set; }

        public bool HasMetadata => Torrent != null;

        /// <summary>
        /// The infohashes for the torrent managed by this manager.
        /// If the manager was created with a MagnetLink, this will be the InfoHashes from the link.
        /// If the manager was created with a Torrent file, this will be the InfoHashes from the torrent.
        /// If metadata has been retrieved for a MagnetLink, this will be the InfoHashes from the retrieved metadata.
        /// </summary>
        public InfoHashes InfoHashes => Torrent?.InfoHashes ?? MagnetLink.InfoHashes;

        /// <summary>
        /// The path to the .torrent metadata used to create the TorrentManager. Typically stored within the <see cref="EngineSettings.MetadataCacheDirectory"/> directory.
        /// </summary>
        public string MetadataPath { get; }

        /// <summary>
        /// True if this torrent has activated special processing for the final few pieces
        /// </summary>
        public bool IsInEndGame => State == TorrentState.Downloading && PieceManager.InEndgameMode;

        public ConnectionMonitor Monitor { get; }

        /// <summary>
        /// The number of peers that this torrent instance is connected to
        /// </summary>
        public int OpenConnections => Peers.ConnectedPeers.Count;

        /// <summary>
        /// The time the last announce to the DHT occurred
        /// </summary>
        public DateTime LastDhtAnnounce { get; private set; }

        /// <summary>
        /// Internal timer used to trigger Dht announces every interval seconds.
        /// </summary>
        internal ValueStopwatch LastDhtAnnounceTimer;

        /// <summary>
        /// The time the last announce using Local Peer Discovery occurred
        /// </summary>
        public DateTime LastLocalPeerAnnounce { get; private set; }

        /// <summary>
        /// Internal timer used to trigger Local PeerDiscovery announces every interval.
        /// </summary>
        internal ValueStopwatch LastLocalPeerAnnounceTimer;

        public MagnetLink MagnetLink { get; }

        internal BitField PartialProgressSelector { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public PeerManager Peers { get; }


        /// <summary>
        /// The piecemanager for this TorrentManager
        /// </summary>
        public PieceManager PieceManager { get; }


        /// <summary>
        /// The inactive peer manager for this TorrentManager
        /// </summary>
        /// <summary>
        /// The download progress (0-100.0) considering only files with priority higher than DoNotDownload.
        /// </summary>
        internal InactivePeerManager InactivePeerManager { get; }

        /// <summary>
        /// The download progress in percent (0 -> 100.0) for the files whose priority
        /// is not set to <see cref="Priority.DoNotDownload"/>. If every file is marked
        /// as <see cref="Priority.DoNotDownload"/> then this returns 0. If no file is
        /// marked as 'DoNotDownload' then this returns the same value as <see cref="Progress"/>.
        /// </summary>
        public double PartialProgress {
            get {
                if (!HasMetadata)
                    return Progress;

                if (PartialProgressSelector.TrueCount == 0)
                    return 0;

                // This is an optimisation so we can fastpath the Bitfield operations when
                // all files are marked as downloadable.
                if (PartialProgressSelector.TrueCount == Bitfield.Length)
                    return Progress;

                int totalTrue = Bitfield.CountTrue (PartialProgressSelector);
                return (totalTrue * 100.0) / PartialProgressSelector.TrueCount;
        /// <summary>
        /// The overall download progress (0-100.0) considering all files in the torrent.
        /// </summary>
            }
        }

        /// <summary>
        /// The download progress in percent (0 -> 100.0). This includes all files, even
        /// if they are marked as <see cref="Priority.DoNotDownload"/>. This will return
        /// '100.0' when all files in the torrent have been downloaded.
        /// </summary>
        public double Progress => Bitfield.PercentComplete;

        /// <summary>
        /// The directory where the downloaded files are stored. For multi-file torrents, this is the parent directory of the torrent's root folder (unless CreateContainingDirectory is false).
        /// </summary>
        /// <summary>
        /// The top level directory where files can be located. If the torrent contains one file, this is the directory where
        /// that file is stored. If the torrent contains two or more files, this value is generated by concatenating <see cref="SavePath"/>
        /// and <see cref="Torrent.Name"/> after replacing all invalid characters with equivalents which are safe to use in file paths.
        /// <see cref="ContainingDirectory"/> will be <see langword="null"/> until the torrent metadata has been downloaded and <see cref="HasMetadata"/> returns
        /// <see langword="true"/>
        /// </summary>
        public string ContainingDirectory {
            get; private set;
        }

        /// <summary>
        /// The final directory path where the torrent data is located after download completion. For single-file torrents, this is the same as SavePath. For multi-file torrents, it's typically SavePath/TorrentName.
        /// This property is only valid after torrent metadata is available.
        /// </summary>
        /// <summary>
        /// If this is a single file torrent, the file will be saved directly inside this directory and <see cref="ContainingDirectory"/> will
        /// be the same as <see cref="SavePath"/>. If this is a multi-file torrent and <see cref="TorrentSettings.CreateContainingDirectory"/>
        /// is set to <see langword="true"/>, all files will be stored in a sub-directory of <see cref="SavePath"/>. The subdirectory name will
        /// <summary>
        /// The settings specific to this torrent, which can override global engine settings.
        /// </summary>
        /// be based on <see cref="Torrent.Name"/>, except invalid characters will be replaced. In this scenario all files will be found within
        /// the directory specified by <see cref="ContainingDirectory"/>.
        /// </summary>
        public string SavePath { get; private set; }

        /// <summary>
        /// The settings for with this TorrentManager
        /// </summary>
        public TorrentSettings Settings { get; private set; }

        /// <summary>
        /// The current state of the TorrentManager (e.g., Stopped, Hashing, Downloading, Seeding).
        /// </summary>
        public TorrentState State => mode.State;

        /// <summary>
        /// The time the torrent manager was started at
        /// </summary>
        public DateTime StartTime { get; private set; }

        /// <summary>
        /// When a <see cref="Torrent"/> or <see cref="MagnetLink"/> has been added using
        /// the 'AddStreamingAsync' methods on <see cref="ClientEngine"/> then this property
        /// will be non-null and streams can be created to access any of the files in the
        /// torrent while they are downloading. These streams are fully seekable, and if you
        /// seek to a position which has not been downloaded already the required pieces will
        /// be prioritised next.
        /// </summary>
        public StreamProvider? StreamProvider { get; internal set; }
        /// <summary>
        /// The loaded Torrent metadata. This will be null if the TorrentManager was created with a MagnetLink and the metadata has not yet been downloaded.
        /// </summary>

        /// <summary>
        /// The tracker connection associated with this TorrentManager
        /// </summary>
        public ITrackerManager TrackerManager { get; private set; }

        /// <summary>
        /// The Torrent contained within this TorrentManager
        /// </summary>
        public Torrent? Torrent { get; private set; }

        public string Name {
            get {
                if (Torrent?.Name is { Length: > 0 })
                    return Torrent.Name;
                if (MagnetLink?.Name is { Length: > 0 })
                    return MagnetLink.Name;
                return "";
            }
        }

        ITorrentInfo? ITorrentManagerInfo.TorrentInfo => Torrent;

        /// <summary>
        /// The number of peers that we are currently uploading to
        /// </summary>
        public int UploadingTo { get; internal set; }

        RateLimiter UploadLimiter { get; }

        internal RateLimiterGroup UploadLimiters { get; }

        public bool IsInitialSeeding => Mode is InitialSeedingMode;

        internal BitField PendingV2PieceHashes { get; private set; }
        public IPieceHashes PieceHashes { get; set; }

        #endregion

        #region Constructors

        internal TorrentManager (ClientEngine engine, Torrent torrent, string savePath, TorrentSettings settings)
            : this (engine, torrent, null, savePath, settings)
        {
            SetMetadata (torrent);
        }

        internal TorrentManager (ClientEngine engine, MagnetLink magnetLink, string savePath, TorrentSettings settings)
            : this (engine, null, magnetLink, savePath, settings)
        {
        }

        TorrentManager (ClientEngine engine, Torrent? torrent, MagnetLink? magnetLink, string savePath, TorrentSettings settings)
        {
            Engine = engine;
            Files = Array.Empty<ITorrentManagerFile> ();

            if (magnetLink?.PublicKeyHex != null)
            {
                // BEP46 Mutable Torrent
                MutablePublicKey = (BEncodedString)HexDecode(magnetLink.PublicKeyHex);
                if (magnetLink.SaltHex != null)
                    MutableSalt = new BEncodedString(HexDecode(magnetLink.SaltHex)); // Correctly create BEncodedString
                // Initial sequence number is unknown, will be set on first successful Get
                LastKnownSequenceNumber = null;
                // InfoHashes remains null for BEP46 until first update. It's read-only anyway.
                // InfoHashes remains null for BEP46 until first update
                // Torrent object will also be null initially
                Torrent = null;
                LastMutableUpdateCheckTimer.Restart(); // Start the timer here
            } else {
                 // Traditional torrent/magnet link
                 // InfoHashes property getter handles null Torrent. No assignment needed here.
            }

            // Assign the provided magnet link or create one from the torrent.
            // For BEP46 links, MagnetLink.InfoHashes will be null initially.
            MagnetLink = magnetLink ?? new MagnetLink (torrent!.InfoHashes, torrent.Name, torrent.AnnounceUrls.SelectMany (t => t).ToArray (), null, torrent.Size);
            PieceHashes = new PieceHashes (null, null);
            Settings = settings;
            Torrent = torrent;

            ContainingDirectory = "";

            MetadataTask = new TaskCompletionSource<Torrent> ();
            // For BEP46 links, InfoHashes is null initially. MetadataPath isn't relevant until metadata is fetched.
            MetadataPath = InfoHashes is null ? "" : engine.Settings.GetMetadataPath (InfoHashes);

            var announces = Torrent?.AnnounceUrls;
            if (announces == null) {
                announces = new List<IList<string>> ();
                if (magnetLink?.AnnounceUrls != null)
                    announces.Add (magnetLink.AnnounceUrls);
            }
            TrackerManager = new TrackerManager (engine.Factories, new TrackerRequestFactory (this), announces, torrent?.IsPrivate ?? false);
            SetTrackerManager (TrackerManager);

            PendingV2PieceHashes = new BitField (Torrent != null ? Torrent.PieceCount : 1).SetAll (true);
            MutableBitField = new BitField (Torrent != null ? Torrent.PieceCount: 1);
            PartialProgressSelector = new BitField (Torrent != null ? Torrent.PieceCount : 1);
            UnhashedPieces = new BitField (Torrent != null ? Torrent.PieceCount : 1).SetAll (true);
            SavePath = string.IsNullOrEmpty (savePath) ? Environment.CurrentDirectory : Path.GetFullPath (savePath);
            finishedPieces = new Queue<int> ();
            Monitor = new ConnectionMonitor ();
            InactivePeerManager = new InactivePeerManager (this, engine.ConnectionManager);
            Peers = new PeerManager ();
            PieceManager = new PieceManager (this);

            mode = new StoppedMode ();
            DownloadLimiter = new RateLimiter ();
            DownloadLimiters = new RateLimiterGroup {
                new PauseLimiter(this),
                DownloadLimiter
            };

            UploadLimiter = new RateLimiter ();
            UploadLimiters = new RateLimiterGroup {
                new PauseLimiter(this),
                UploadLimiter
            };
        }



        #endregion


        #region Public Methods

        internal void ChangePicker (IPieceRequester requester)
        {
            if (requester == null)
                throw new ArgumentNullException (nameof (requester));

            PieceManager.ChangePicker (requester);
            if (requester is IStreamingPieceRequester streamingRequester)
                StreamProvider = new StreamProvider (this, streamingRequester);
            else
                StreamProvider = null;
        }

        /// <summary>
        /// Changes the active piece picker. This can be called when the manager is running, or when it is stopped.
        /// <summary>
        /// Retrieves a list of currently connected peers.
        /// </summary>
        /// <returns>A list of PeerId objects representing connected peers.</returns>
        /// </summary>
        /// <param name="requester">The new picker to use.</param>
        /// <returns></returns>
        public async Task ChangePickerAsync (IPieceRequester requester)
        {
            await ClientEngine.MainLoop;
            ChangePicker (requester);
        }

        internal void Dispose ()
        {
            if (Disposed)
                return;

            Disposed = true;
        }


        /// <summary>
        /// Overrridden. Returns the name of the torrent.
        /// </summary>
        /// <returns></returns>
        public override string ToString ()
        {
            return Torrent == null ? "<Metadata Mode>" : Torrent.Name;
        }

        public string LogName => Torrent == null ? (InfoHashes?.ToString () ?? MutablePublicKey?.ToHex () ?? "<No ID>") : $"{Torrent.Name} ({InfoHashes})";

        public async Task<List<PeerId>> GetPeersAsync ()
        {
        /// <summary>
        /// Performs a full hash check of the torrent data, verifying the integrity of downloaded pieces against the torrent metadata.
        /// This ignores any existing fast resume data.
        /// </summary>
        /// <param name="autoStart">If true, the torrent will automatically start downloading/seeding after the hash check completes successfully.</param>
        /// <returns>A task representing the asynchronous hash check operation.</returns>
            await ClientEngine.MainLoop;
            return new List<PeerId> (Peers.ConnectedPeers);
        }

        /// <summary>
        /// Performs a full hash check, ignoring any previously loaded Fast Resume data or previous hash checks.
        /// </summary>
        /// <param name="autoStart">True if a the TorrentManager should be started as soon as the hashcheck completes.</param>
        public Task HashCheckAsync (bool autoStart)
        {
            return HashCheckAsync (autoStart, true);
        }

        internal async Task HashCheckAsync (bool autoStart, bool setStoppedModeWhenDone)
        {
            if (!HasMetadata)
                throw new TorrentException ("A hashcheck cannot be performed if the TorrentManager was created with a Magnet link and the metadata has not been downloaded.");

            await ClientEngine.MainLoop;
            if (!Mode.CanHashCheck)
                throw new TorrentException (
                    $"A hashcheck can only be performed when the manager is stopped. State is: {State}");

            CheckRegisteredAndDisposed ();
            StartTime = DateTime.Now;

            // An IgnoringPicker is created to ensure pieces which *have not* been hash checked
            // are not requested from other peers. The intention is that files marked as DoNotDownload
            // will not be hashed, or downloaded.
            UnhashedPieces.SetAll (true);

            var hashingMode = new HashingMode (this, Engine!.DiskManager);
            Mode = hashingMode;

            try {
                var start = ValueStopwatch.StartNew ();
                await hashingMode.WaitForHashingToComplete ();
                logger.Info ($"{this} hashing took {start.Elapsed}");
                hashingMode.Token.ThrowIfCancellationRequested ();
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                TrySetError (Reason.ReadFailure, ex);
                return;
            }

            HashChecked = true;

            if (autoStart) {
                await StartAsync ();
        /// <summary>
        /// Moves a single file from its current location to a new path within the same filesystem.
        /// This operation can only be performed when the torrent is stopped.
        /// </summary>
        /// <param name="file">The torrent file to move.</param>
        /// <param name="path">The full path to the new location, including the filename.</param>
        /// <returns>A task representing the asynchronous move operation.</returns>
            } else if (setStoppedModeWhenDone) {
                await MaybeWriteFastResumeAsync ();

                Mode = new StoppedMode ();
            }
        }

        public async Task MoveFileAsync (ITorrentManagerFile file, string path)
        {
            Check.File (file);
            Check.PathNotEmpty (path);
            CheckRegisteredAndDisposed ();
            CheckMetadata ();

            try {
                var paths = TorrentFileInfo.GetNewPaths (Path.GetFullPath (path), Engine!.Settings.UsePartialFiles, file.FullPath == file.DownloadCompleteFullPath);
        /// <summary>
        /// Moves all files in the torrent to a new save location.
        /// This operation can only be performed when the torrent is stopped.
        /// </summary>
        /// <param name="newRoot">The new root directory where the torrent data should be moved. The torrent's containing directory structure will be preserved under this root.</param>
        /// <param name="overWriteExisting">If true, existing files or directories at the destination will be overwritten.</param>
        /// <returns>A task representing the asynchronous move operation.</returns>
                await Engine!.DiskManager.MoveFileAsync (file, paths);
            } catch (Exception ex) {
                TrySetError (Reason.WriteFailure, ex);
                throw;
            }
        }

        public async Task MoveFilesAsync (string newRoot, bool overWriteExisting)
        {
            CheckRegisteredAndDisposed ();
            CheckMetadata ();

            if (State != TorrentState.Stopped)
                throw new TorrentException ("Cannot move files when the torrent is active");

            try {
                await Engine!.DiskManager.MoveFilesAsync (Files, newRoot, overWriteExisting);
                ContainingDirectory = SavePath = newRoot;
            } catch (Exception ex) {
                TrySetError (Reason.WriteFailure, ex);
            }
        }

        /// <summary>
        /// Pauses the TorrentManager, preventing further data transfer until StartAsync is called.
        /// If the manager is currently hashing, the hashing process will be paused.
        /// </summary>
        /// <returns>A task representing the asynchronous pause operation.</returns>
        public async Task PauseAsync ()
        {
            await ClientEngine.MainLoop;
            CheckRegisteredAndDisposed ();

            if (Mode is HashingMode hashing) {
                hashing.Pause ();
            } else if (Mode is DownloadMode) {
                Mode = new PausedMode (this, Engine!.DiskManager, Engine.ConnectionManager, Engine.Settings);
            }
        }

        internal void SetMetadata (Torrent torrent)
        {
            Torrent = torrent;
            foreach (PeerId id in new List<PeerId> (Peers.ConnectedPeers))
                Engine!.ConnectionManager.CleanupSocket (this, id, DisconnectReason.MetadataReset);
            MutableBitField = new BitField (Torrent.PieceCount);
            PartialProgressSelector = new BitField (Torrent.PieceCount).SetAll (true);
            PendingV2PieceHashes = new BitField (Torrent.PieceCount);
            UnhashedPieces = new BitField (Torrent.PieceCount).SetAll (true);

            // Now we know the torrent name, use it as the base directory name when it's a multi-file torrent
            if (Torrent.Files.Count == 1 || !Settings.CreateContainingDirectory)
                ContainingDirectory = SavePath;
            else {
                new TorrentPath (Torrent.Name);
                ContainingDirectory = Path.GetFullPath (Path.Combine (SavePath, TorrentFileInfo.PathEscape (Torrent.Name)));
            }

            if (!ContainingDirectory.StartsWith (SavePath))
                throw new InvalidOperationException ($"The containing directory path '{ContainingDirectory}' must be a subdirectory of '{SavePath}'.");

            // All files marked as 'Normal' priority by default so 'PartialProgressSelector'
            // should be set to 'true' for each piece as all files are being downloaded.
            Files = Torrent.Files.Select (file => {

                // Generate the paths when 'UsePartialFiles' is enabled.
                var paths = TorrentFileInfo.GetNewPaths (Path.Combine (ContainingDirectory, TorrentFileInfo.PathAndFileNameEscape (file.Path.ToString())), usePartialFiles: true, isComplete: true);
                var downloadCompleteFullPath = paths.completePath;
                var downloadIncompleteFullPath = paths.incompletePath;

                // FIXME: Is this the best place to futz with actually moving files?
                if (!Engine!.Settings.UsePartialFiles) {
                    if (File.Exists (downloadIncompleteFullPath) && !File.Exists (downloadCompleteFullPath))
                        File.Move (downloadIncompleteFullPath, downloadCompleteFullPath);

                    downloadIncompleteFullPath = downloadCompleteFullPath;
                }

                var currentPath = File.Exists (downloadCompleteFullPath) ? downloadCompleteFullPath : downloadIncompleteFullPath;
                var torrentFileInfo = new TorrentFileInfo (file, currentPath);
                torrentFileInfo.UpdatePaths ((currentPath, downloadCompleteFullPath, downloadIncompleteFullPath));
                return torrentFileInfo;
        /// <summary>
        /// Performs a Local Peer Discovery announce to find peers on the local network.
        /// This is typically called periodically by the engine but can be invoked manually.
        /// </summary>
        /// <returns>A task representing the asynchronous announce operation.</returns>
            }).Cast<ITorrentManagerFile> ().ToList ().AsReadOnly ();

            PieceHashes = Torrent.CreatePieceHashes ();
            // If this torrent is supposed to have V2 hashes *and* we do not have them, mark them as missing.
            // This will cause all pieces to be treated as 'not downloadable' and no peers will be treated as interesting.
            // Otherwise set everything here to 'false' so the engine knows all pieces can be requested/verified.
            //
            // This will be set to 'false' when the V2 hashes have been fully requested, allowing all pieces to be
            // downloaded normally.
            PendingV2PieceHashes.SetAll (Torrent.InfoHashes.V2 != null && !PieceHashes.HasV2Hashes);
            PieceManager.Initialise ();
            MetadataTask.SetResult (Torrent);
        }

        /// <summary>
        /// Starts the TorrentManager. If the manager was paused, it resumes.
        /// If it was stopped, it will initiate a hash check (if needed) and then start downloading/seeding.
        /// If it was created with a MagnetLink and has no metadata, it will enter MetadataMode to download the metadata.
        /// </summary>
        /// <returns>A task representing the asynchronous start operation.</returns>
        public async Task StartAsync ()
            => await StartAsync (false);

        internal async Task StartAsync (bool metadataOnly)
        {
        /// <summary>
        /// Performs a DHT announce and requests peers for the torrent.
        /// This is typically called periodically by the engine but can be invoked manually.
        /// </summary>
        /// <returns>A task representing the asynchronous announce operation.</returns>
            await ClientEngine.MainLoop;

            if (Mode is StoppingMode)
                throw new TorrentException ("The manager cannot be restarted while it is in the Stopping state.");
            if (Mode is StartingMode)
                throw new TorrentException ("The manager cannot be started a second time while it is already in the Starting state.");

            CheckRegisteredAndDisposed ();

            // If the torrent was "paused", then just update the state to Downloading and forcefully
            // make sure the peers begin sending/receiving again
            if (State == TorrentState.Paused) {
                Mode = new DownloadMode (this, Engine!.DiskManager, Engine.ConnectionManager, Engine.Settings);
            } else if (Mode is HashingMode hashing && !HashChecked) {
                if (State == TorrentState.HashingPaused)
                    hashing.Resume ();
            } else {
                await Engine!.StartAsync ();
                StartTime = DateTime.Now;
                if (!HasMetadata) {
                    Mode = new MetadataMode (this, Engine.DiskManager, Engine.ConnectionManager, Engine.Settings, MetadataPath, metadataOnly);
                } else {
                    var startingMode = new StartingMode (this, Engine.DiskManager, Engine.ConnectionManager, Engine.Settings);
                    Mode = startingMode;
                    _ = startingMode.WaitForStartingToComplete ();
                }
            }
        }

        public async ReusableTask LocalPeerAnnounceAsync ()
        {
            await ClientEngine.MainLoop;

            if (Engine != null && CanUseLocalPeerDiscovery && (!LastLocalPeerAnnounceTimer.IsRunning || LastLocalPeerAnnounceTimer.Elapsed > Engine.LocalPeerDiscovery.MinimumAnnounceInternal)) {
                LastLocalPeerAnnounce = DateTime.Now;
                LastLocalPeerAnnounceTimer.Restart ();

                var endPoints = Engine.PeerListeners.Select (t => t.LocalEndPoint!).Where (t => t != null);
                foreach (var endpoint in endPoints) {
                   // Announce V1 hash if available, as LPD is primarily V1.
                   // Otherwise, announce truncated V2 hash for V2-only torrents.
                   if (InfoHashes == null) {
                       Console.WriteLine($"[Warning] {LogName} - Cannot perform Local Peer Announce as InfoHashes is null.");
                       return;
                   }
                   if (InfoHashes.V1 != null)
                        await Engine.LocalPeerDiscovery.Announce (InfoHashes.V1, endpoint);
                   else if (InfoHashes.V2 != null) // Only announce V2 if V1 is not present
                        await Engine.LocalPeerDiscovery.Announce (InfoHashes.V2.Truncate(), endpoint);
                }
            }
        }

        /// <summary>
        /// <summary>
        /// Updates the settings used by this TorrentManager.
        /// </summary>
        /// <param name="settings">The new settings to apply.</param>
        /// <returns>A task representing the asynchronous settings update.</returns>
        /// Perform an announce using the <see cref="ClientEngine.DhtEngine"/> to retrieve more peers. The
        /// returned task completes as soon as the Dht announce begins.
        /// </summary>
        /// <returns></returns>
        public async ReusableTask DhtAnnounceAsync ()
        {
            await ClientEngine.MainLoop;

            if (CanUseDht && Engine != null && (!LastDhtAnnounceTimer.IsRunning || LastDhtAnnounceTimer.Elapsed > Engine.DhtEngine.MinimumAnnounceInterval)) {
                LastDhtAnnounce = DateTime.UtcNow;
                LastDhtAnnounceTimer.Restart ();
                // Announce based on the torrent protocol
                // Null check InfoHashes first, although CanUseDht should imply it's available if Torrent is not null.
                if (InfoHashes == null) {
                    Console.WriteLine($"[Warning] {LogName} - Cannot perform DHT announce as InfoHashes is null.");
                    return;
                }

                if (InfoHashes.Protocol == TorrentProtocol.V2 || InfoHashes.Protocol == TorrentProtocol.Hybrid)
                    Engine.DhtEngine.GetPeers (InfoHashes.V2!.Truncate ()); // V2 must be non-null here
                if (InfoHashes.Protocol == TorrentProtocol.V1 || InfoHashes.Protocol == TorrentProtocol.Hybrid)
                    Engine.DhtEngine.GetPeers (InfoHashes.V1!); // V1 must be non-null here
                    Engine.DhtEngine.GetPeers (InfoHashes.V1!); // V1 must be non-null here
                if (InfoHashes.Protocol == TorrentProtocol.V2 || InfoHashes.Protocol == TorrentProtocol.Hybrid)
                    Engine.DhtEngine.GetPeers (InfoHashes.V2!.Truncate ()); // V2 must be non-null here
                if (InfoHashes.Protocol == TorrentProtocol.V1 || InfoHashes.Protocol == TorrentProtocol.Hybrid)
                    Engine.DhtEngine.GetPeers (InfoHashes.V1!); // V1 must be non-null here
            }
        }

        /// <summary>
        /// Forces an immediate check for updates to a mutable torrent (BEP46) via DHT.
        /// This bypasses the regular timed check interval.
        /// </summary>
        /// <returns>A task representing the asynchronous update check operation.</returns>
        public async Task ForceMutableUpdateCheckAsync()
        {
            await ClientEngine.MainLoop;
            CheckRegisteredAndDisposed();
            if (MutablePublicKey == null)
                throw new InvalidOperationException("This is not a mutable torrent.");

            logger.InfoFormatted("BEP46 for {0}: Forcing mutable update check.", this.LogName);
            await PerformMutableUpdateCheckAsync();
        }


        /// <summary>
        /// Stops the TorrentManager. Data transfer will cease, and a 'stopped' announce will be sent to trackers.
        /// The returned task completes when the manager has fully stopped.
        /// </summary>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        public Task StopAsync ()
        {
            return StopAsync (Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Stops the TorrentManager. The returned task completes as soon as the manager has fully stopped. The final
        /// tracker announce will be limited to the maximum of either 2 seconds or <paramref name="timeout"/> seconds.
        /// </summary>
        public async Task StopAsync (TimeSpan timeout)
        {
            await ClientEngine.MainLoop;

            if (Mode is StoppingMode)
                throw new TorrentException ("The manager cannot be stopped while it is already in the Stopping state.");

            if (State == TorrentState.Error) {
                Error = null;
                Mode = new StoppedMode ();
                await Engine!.StopAsync ();
            } else if (State != TorrentState.Stopped) {
                var stoppingMode = new StoppingMode (this, Engine!.DiskManager, Engine.ConnectionManager);
                Mode = stoppingMode;
                await stoppingMode.WaitForStoppingToComplete (timeout);
        /// <summary>
        /// Returns a task that completes when the torrent metadata is available. If the metadata is already available, the task completes immediately.
        /// This is useful when the TorrentManager is created from a MagnetLink.
        /// </summary>
        /// <returns>A task that completes when the metadata is downloaded.</returns>

                stoppingMode.Token.ThrowIfCancellationRequested ();
                Mode = new StoppedMode ();
                await MaybeWriteFastResumeAsync ();
                await Engine.StopAsync ();
            }
        /// <summary>
        /// Returns a task that completes when the torrent metadata is available or when the cancellation token is triggered.
        /// If the metadata is already available, the task completes immediately.
        /// </summary>
        /// <param name="token">The cancellation token.</param>
        /// <returns>A task that completes when the metadata is downloaded or cancellation is requested.</returns>
        }

        public async Task UpdateSettingsAsync (TorrentSettings settings)
        {
            await ClientEngine.MainLoop;
            Settings = settings;
        }

        /// <summary>
        /// Waits for the metadata to be available
        /// </summary>
        /// <returns></returns>
        public Task WaitForMetadataAsync ()
            => WaitForMetadataAsync (CancellationToken.None);

        public async Task WaitForMetadataAsync (CancellationToken token)
        {
            // Fast path (if possible).
            if (HasMetadata)
                return;

            var tcs = new TaskCompletionSource<object> ();
            using var registration = token.Register (tcs.SetCanceled);

            // Wait for the token to be cancelled *or* the metadata is received.
            // Await the returned task so the OperationCancelled exception propagates as
            // expected if the token was cancelled. The try/catch is so that we
        /// <summary>
        /// Adds a single peer to the list of available peers for this torrent.
        /// </summary>
        /// <param name="peer">The peer to add.</param>
        /// <returns>True if the peer was added, false if the peer was already known or invalid.</returns>
            // will always throw an OperationCancelled instead of, sometimes, propagating
            // a TaskCancelledException.
            try {
        /// <summary>
        /// Adds multiple peers to the list of available peers for this torrent.
        /// </summary>
        /// <param name="peers">The collection of peers to add.</param>
        /// <returns>The number of peers that were successfully added.</returns>
                await (await Task.WhenAny (MetadataTask.Task, tcs.Task));
            } catch {
                token.ThrowIfCancellationRequested ();
                throw;
            }
        }

        #endregion


        #region Internal Methods

        public async Task<bool> AddPeerAsync (PeerInfo peer)
            => await AddPeersAsync (new[] { peer }) > 0;

        public async Task<int> AddPeersAsync (IEnumerable<PeerInfo> peers)
        {
            await ClientEngine.MainLoop;
            return AddPeers (peers, prioritise: false, fromTracker: false);
        }

        internal int AddPeers (IEnumerable<PeerInfo> peers, bool prioritise, bool fromTracker)
        {
            Check.Peers (peers);
            if (HasMetadata && Torrent!.IsPrivate && !fromTracker)
                throw new InvalidOperationException ("You cannot add external peers to a private torrent");

            int count = 0;
            foreach (PeerInfo p in peers)
                count += AddPeer (p, prioritise) ? 1 : 0;
            return count;
        }

        bool AddPeer (PeerInfo peerInfo, bool prioritise)
        {
            if (peerInfo is null)
                throw new ArgumentNullException (nameof (peerInfo));

            var peer = new Peer (peerInfo) {
                IsSeeder = peerInfo.MaybeSeeder,
            };

            if (Peers.Contains (peer))
                return false;

            // Ignore peers in the inactive list
            if (InactivePeerManager.InactivePeerList.Contains (peer.Info.ConnectionUri)) {
                logger.Debug ($"{this} - ignoring {peer.Info.ConnectionUri} as it is inactive");
                return false;
            }

            if (Engine!.PeerId.Equals (peer.Info.PeerId))
                return false;

            if (Peers.TotalPeers < Settings.MaximumPeerDetails) {
                if (prioritise)
                    Peers.AvailablePeers.Insert (0, peer);
                else
                    Peers.AvailablePeers.Add (peer);
            } else {
                bool successful = false;
                for (int i = 0; i < Peers.AvailablePeers.Count; i++) {
                    if (Peers.AvailablePeers[i].MaybeStale) {
                        Peers.AvailablePeers[i] = peer;
                        successful = true;
                        break;
                    }
                }
                if (!successful)
                    return false;
            }
            OnPeerFound?.Invoke (this, new PeerAddedEventArgs (this, peerInfo));
            // When we successfully add a peer we try to connect to the next available peer
            return true;
        }


        internal void RaisePeerConnected (PeerId id)
            => PeerConnected?.InvokeAsync (this, new PeerConnectedEventArgs (this, id));

        internal void RaisePeerDisconnected (PeerId id, DisconnectReason reason)
            => PeerDisconnected?.InvokeAsync (this, new PeerDisconnectedEventArgs (this, id, reason));

        internal void RaisePeersFound (PeersAddedEventArgs args)
        {
            PeersFound?.InvokeAsync (this, args);
        }

        public void UpdatePieceHashStatus (int index, bool hashPassed, int hashed, int totalHashing)
        {
            OnPieceHashed (index: index, hashPassed: hashPassed, piecesHashed: hashed, totalToHash: totalHashing);
            UnhashedPieces[index] = hashed == 0;
        }

        internal void OnPieceHashed (int index, bool hashPassed)
            => OnPieceHashed (index, hashPassed, 1, 1);

        internal void OnPieceHashed (int index, bool hashPassed, int piecesHashed, int totalToHash)
        {
            MutableBitField[index] = hashPassed;
            // The PiecePickers will no longer ignore this piece as it has now been hash checked.
            UnhashedPieces[index] = false;

            var files = Files;
            var fileIndex = files.FindFileByPieceIndex (index);
            for (int i = fileIndex; i < files.Count && files[i].StartPieceIndex <= index; i++) {
                // Empty files always have all bits set to 'true' as they're treated as being downloaded as soon as they exist on disk.
                if (files[i].Length == 0)
                    continue;
                ((TorrentFileInfo) files[i]).BitField[index - files[i].StartPieceIndex] = hashPassed;

                // If we're only hashing 1 piece then we can start moving files now. This occurs when a torrent
                // is actively downloading.
                if (totalToHash == 1)
                    _ = RefreshPartialDownloadFilePaths (i, 1, Engine!.Settings.UsePartialFiles);
            }

            // If we're hashing many pieces, wait for the final piece to be hashed, then start trying to move files.
            // This occurs when we're hash checking, or loading, torrents.
            if (totalToHash > 1 && piecesHashed == totalToHash)
                _ = RefreshPartialDownloadFilePaths (0, files.Count, Engine!.Settings.UsePartialFiles);

            if (hashPassed) {
                List<PeerId> connected = Peers.ConnectedPeers;
                for (int i = 0; i < connected.Count; i++)
                    connected[i].IsAllowedFastPieces.Remove (index);
            }

            lock (argsCache) {
                if (PieceHashed != null)
                    argsCache.Enqueue (new PieceHashedEventArgs (this, index, hashPassed, piecesHashed, totalToHash));

                if (argsCache.Count == 1)
                    InvokePieceHashedAsync ();
            }

            if (Mode is DownloadMode downloadMode && Bitfield.AllTrue)
                _ = downloadMode.UpdateSeedingDownloadingState ();
        }

        Queue<PieceHashedEventArgs> argsCache = new Queue<PieceHashedEventArgs> ();
        async void InvokePieceHashedAsync ()
        {
            await new ThreadSwitcher ();

            while (true) {
                PieceHashedEventArgs args;
                lock (argsCache) {
                    if (argsCache.Count == 0)
                        return;
                    args = argsCache.Dequeue ();
                }
                try {
                    PieceHashed?.Invoke (this, args);
                } catch (Exception e) {
                    logger.Error($"{this.Name} - Exception in event handler: {e.Message}");
                    logger.Debug ($"{e}");
                }
            }
        }


        internal async ReusableTask UpdateUsePartialFiles (bool usePartialFiles)
        {
            await RefreshPartialDownloadFilePaths (0, Files.Count, usePartialFiles);
        }

        internal async ReusableTask RefreshPartialDownloadFilePaths (int fileStartIndex, int count, bool usePartialFiles)
        {
            var files = Files;
            List<Task>? tasks = null;
            for (int i = fileStartIndex; i < fileStartIndex + count; i++) {
                var current = files[i].FullPath;
                var completePath = files[i].DownloadCompleteFullPath;
                var incompletePath = files[i].DownloadCompleteFullPath + (usePartialFiles ? TorrentFileInfo.IncompleteFileSuffix : "");

                if (files[i].BitField.AllTrue && files[i].FullPath != completePath) {
                    tasks ??= new List<Task> ();
                    tasks.Add (Engine!.DiskManager.MoveFileAsync (files[i], (completePath, completePath, incompletePath)));
                } else if (!files[i].BitField.AllTrue && files[i].FullPath != incompletePath) {
                    tasks ??= new List<Task> ();
                    tasks.Add (Engine!.DiskManager.MoveFileAsync (files[i], (incompletePath, completePath, incompletePath)));
                }
            }
            if (tasks != null)
                await Task.WhenAll (tasks).ConfigureAwait (false);
        }

        internal void RaiseTorrentStateChanged (TorrentStateChangedEventArgs e)
        {
            TorrentStateChanged?.InvokeAsync (this, e);
        }

        /// <summary>
        /// Raise the connection attempt failed event
        /// </summary>
        /// <param name="args"></param>
        internal void RaiseConnectionAttemptFailed (ConnectionAttemptFailedEventArgs args)
        {
            ConnectionAttemptFailed?.InvokeAsync (this, args);
        }

        internal void UpdateLimiters ()
        {
            if (Engine != null) {
                DownloadLimiter.UpdateChunks (Settings.MaximumDownloadRate);
                UploadLimiter.UpdateChunks (Settings.MaximumUploadRate);
            }
        }
        #endregion Internal Methods


        #region Private Methods

        void CheckMetadata ()
        {
            if (!HasMetadata)
                throw new InvalidOperationException ("This action cannot be performed until metadata has been retrieved");
        }

        void CheckRegisteredAndDisposed ()
        {
            if (Disposed)
                throw new InvalidOperationException ("This TorrentManager has been removed from it's Engine.");
            if (Engine!.Disposed)
                throw new InvalidOperationException ("The registered engine has been disposed");
        }

        public async Task MarkHashedAsync ()
        {
            await ClientEngine.MainLoop;

            CheckMetadata ();
            if (State != TorrentState.Stopped)
                throw new InvalidOperationException ("Can only change hashing status when the torrent is stopped");

            UnhashedPieces.SetAll (false);
            HashChecked = true;
        }

        /// <summary>
        /// Attempts to load the provided fastresume data. Several validations are performed during this, such as ensuring
        /// files which have validated pieces actually exist on disk, and the length of those files is correct. If any validation
        /// fails, the <see cref="HashChecked"/> boolean will not be set to true, and <see cref="HashCheckAsync(bool)"/> will need
        /// to be run to re-averify the file contents.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task LoadFastResumeAsync (FastResume data)
        {
            if (data == null)
                throw new ArgumentNullException (nameof (data));

            await ClientEngine.MainLoop;

            CheckMetadata ();
            if (State != TorrentState.Stopped)
                throw new InvalidOperationException ("Can only load FastResume when the torrent is stopped");

            // Fast resume data can be a V1 hash or a V2 hash.
            // At some point in the future the InfoHashes object could serialize both the v1
            // and v2 hashes to the BEncodedDictionary interchange format... but probably no need?
            if ((data.InfoHashes.V1 != null && !InfoHashes.Contains (data.InfoHashes.V1)) ||
                (data.InfoHashes.V2 != null && !InfoHashes.Contains (data.InfoHashes.V2)) ||
                (Torrent!.PieceCount != data.Bitfield.Length))
                throw new ArgumentException ("The fast resume data does not match this torrent", "fastResumeData");

            for (int i = 0; i < Torrent.PieceCount; i++)
                OnPieceHashed (i, data.Bitfield[i], i + 1, Torrent.PieceCount);

            UnhashedPieces.From (data.UnhashedPieces);

            await RefreshAllFilesCorrectLengthAsync ();
            HashChecked = true;
        }

        internal async ReusableTask RefreshAllFilesCorrectLengthAsync ()
        {
            var allFilesCorrectLength = true;
            foreach (TorrentFileInfo file in Files) {
                var maybeLength = await Engine!.DiskManager.GetLengthAsync (file);

                // Empty files aren't stored in fast resume data because it's as easy to just check if they exist on disk.
                if (file.Length == 0)
                    file.BitField[0] = maybeLength.HasValue;

                // If any file doesn't exist, or any file is too large, indicate that something is wrong.
                // If files exist but are too short, then we can assume everything is fine and the torrent just
                // needs to be downloaded.
                if (file.Priority != Priority.DoNotDownload && (!maybeLength.HasValue || maybeLength > file.Length))
                    allFilesCorrectLength = false;
            }
            AllFilesCorrectLength = allFilesCorrectLength;
        }

        public async Task<FastResume> SaveFastResumeAsync ()
        {
            await ClientEngine.MainLoop;

            CheckMetadata ();
            if (!HashChecked)
                throw new InvalidOperationException ("Fast resume data cannot be created when the TorrentManager has not been hash checked");
            return new FastResume (InfoHashes, Bitfield, UnhashedPieces);
        }

        internal async ReusableTask MaybeDeleteFastResumeAsync ()
        {
            if (!Engine!.Settings.AutoSaveLoadFastResume)
                return;

            try {
                var path = Engine.Settings.GetFastResumePath (InfoHashes);
                if (File.Exists (path))
                    await Task.Run (() => File.Delete (path));
            } catch (Exception e) {
                logger.Exception (e, "Could not delete fast resume file");
            }
        }

        internal async ReusableTask MaybeLoadFastResumeAsync ()
        {
            if (!Engine!.Settings.AutoSaveLoadFastResume || !HasMetadata)
                return;

            await MainLoop.SwitchToThreadpool ();
            var fastResumePath = Engine.Settings.GetFastResumePath (InfoHashes);
            if (File.Exists (fastResumePath) &&
                FastResume.TryLoad (fastResumePath, out FastResume? fastResume) &&
                InfoHashes.Contains (fastResume.InfoHashes.V1OrV2)) {
                await LoadFastResumeAsync (fastResume);
            }
        }

        internal async ReusableTask MaybeWriteFastResumeAsync ()
        {
            if (!Engine!.Settings.AutoSaveLoadFastResume || !HashChecked)
                return;

            ClientEngine.MainLoop.CheckThread ();
            var fastResumeData = (await SaveFastResumeAsync ()).Encode ();

            await MainLoop.SwitchToThreadpool ();
            var fastResumePath = Engine.Settings.GetFastResumePath (InfoHashes);
            var parentDirectory = Path.GetDirectoryName (fastResumePath)!;
            Directory.CreateDirectory (parentDirectory);
            File.WriteAllBytes (fastResumePath, fastResumeData);
        } // End of MaybeWriteFastResumeAsync


        #region BEP46 Update Check

        internal async ReusableTask PerformMutableUpdateCheckAsync ()
        {
            Console.WriteLine($"[TorrentManager {LogName}] Starting PerformMutableUpdateCheckAsync. LastKnownSeq: {LastKnownSequenceNumber?.ToString() ?? "null"}");
            
            // Modified condition to allow mutable torrent updates even when Stopped
            // This is needed for cases when an external system wants to check for updates without starting the torrent
            if (Engine == null || MutablePublicKey == null || 
                (State == TorrentState.Error) || 
                (State == TorrentState.Stopping))  // Removed the TorrentState.Stopped check
            {
                 Console.WriteLine($"[TorrentManager {LogName}] Skipping update check. EngineNull: {Engine == null}, MutablePKNull: {MutablePublicKey == null}, State: {State}");
                return; // Only check if it's a mutable torrent and not in error/stopping state
            }

            try {
                // Reset the timer *after* the check completes to ensure the interval is respected.
                NodeId targetId = MonoTorrent.Dht.DhtEngine.CalculateMutableTargetId(MutablePublicKey, MutableSalt); // Use full namespace
                Console.WriteLine($"[TorrentManager {LogName}] Calling DHT GetAsync with target {targetId} and seq {LastKnownSequenceNumber?.ToString() ?? "null"}");
                Console.WriteLine($"[TorrentManager {LogName}] Calling DHT GetAsync with target {targetId} and seq {LastKnownSequenceNumber?.ToString() ?? "null"}");
                (BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber) = await Engine!.DhtEngine.GetAsync(targetId, LastKnownSequenceNumber); // Pass the last known sequence number
                Console.WriteLine($"[TorrentManager {LogName}] DHT GetAsync returned: ValuePresent={value!=null}, PKPresent={publicKey!=null}, SigPresent={signature!=null}, Seq={sequenceNumber?.ToString() ?? "null"}");
                Console.WriteLine($"[TorrentManager {LogName}] DHT GetAsync returned: ValuePresent={value!=null}, PKPresent={publicKey!=null}, SigPresent={signature!=null}, Seq={sequenceNumber?.ToString() ?? "null"}");

                if (value != null && publicKey != null && signature != null && sequenceNumber.HasValue)
                {
                    Console.WriteLine($"[TorrentManager {LogName}] Received potential update. Current Known Seq: {LastKnownSequenceNumber?.ToString() ?? "null"}, Received Seq: {sequenceNumber.Value}");
                    Console.WriteLine($"[TorrentManager {LogName}] Received potential update. Current Known Seq: {LastKnownSequenceNumber?.ToString() ?? "null"}, Received Seq: {sequenceNumber.Value}");
                    // We already filtered by sequence number in the GetAsync request (by passing LastKnownSequenceNumber),
                    // but we double-check here in case of race conditions or DHT inconsistencies.
                    bool isNewer = !LastKnownSequenceNumber.HasValue || sequenceNumber.Value > LastKnownSequenceNumber.Value;
                    Console.WriteLine($"[TorrentManager {LogName}] Sequence number check (Received > Known): {isNewer}");
                    Console.WriteLine($"[TorrentManager {LogName}] Sequence number check (Received > Known): {isNewer}");

                    if (isNewer)
                    {
                        Console.WriteLine($"[TorrentManager {LogName}] Sequence number is newer. Verifying signature...");
                        Console.WriteLine($"[TorrentManager {LogName}] Sequence number is newer. Verifying signature...");
                        bool signatureValid = VerifyMutableSignature(publicKey, MutableSalt, sequenceNumber.Value, value, signature);
                        Console.WriteLine($"[TorrentManager {LogName}] Signature verification result: {signatureValid}");
                        Console.WriteLine($"[TorrentManager {LogName}] Signature verification result: {signatureValid}");

                        if (signatureValid)
                        {
                            Console.WriteLine($"[TorrentManager {LogName}] Signature valid. Processing update...");
                            Console.WriteLine($"[TorrentManager {LogName}] Signature valid. Processing update...");

                            // Ensure 'value' is a dictionary before proceeding (though BEP46 implies it should be)
                            if (value is BEncodedDictionary vDict) {
                                try {
                                    // Always calculate the InfoHash from the entire bencoded 'v' dictionary.
                                    // This assumes the 'v' dictionary *is* the new 'info' dictionary or
                                    // contains the necessary fields for the SHA1 hash calculation to yield
                                    // the intended new InfoHash.
                                    byte[] encodedInfo = vDict.Encode();
                                    InfoHash calculatedNewInfoHash;
                                    using (var sha1 = System.Security.Cryptography.SHA1.Create()) {
                                        calculatedNewInfoHash = new InfoHash(sha1.ComputeHash(encodedInfo));
                                    }
                                    Console.WriteLine($"[TorrentManager {LogName}] Calculated new InfoHash from 'v': {calculatedNewInfoHash.ToHex()}");

                                    // Update local state and raise event
                                    Console.WriteLine($"[TorrentManager {LogName}] Updating LastKnownSequenceNumber from {LastKnownSequenceNumber?.ToString() ?? "null"} to {sequenceNumber.Value}");
                                    LastKnownSequenceNumber = sequenceNumber.Value;
                                    Console.WriteLine($"[Info] BEP46 update found for {this.LogName}. Calculated New InfoHash: {calculatedNewInfoHash.ToHex()}");
                                    Console.WriteLine($"[TorrentManager {LogName}] Invoking TorrentUpdateAvailable event with hash {calculatedNewInfoHash.ToHex()}");
                                    TorrentUpdateAvailable?.Invoke(this, new TorrentUpdateEventArgs(this, calculatedNewInfoHash));

                                } catch (Exception calcEx) {
                                    // Log error during calculation
                                    Console.WriteLine($"[Error] BEP46 for {this.LogName}: Error calculating new InfoHash or processing update: {calcEx.Message}");
                                    // Do not update sequence number or raise event if processing failed
                                }
                            } else {
                                // This case should ideally not happen if the DHT stores valid BEP46 data.
                                Console.WriteLine($"[Error] BEP46 for {this.LogName}: Received mutable item value 'v' was not a BEncodedDictionary.");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[TorrentManager {LogName}] Signature invalid. Discarding update.");
                            Console.WriteLine($"[Error] BEP46 for {this.LogName}: Received mutable item with invalid signature.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[TorrentManager {LogName}] Received sequence number {sequenceNumber.Value} is not newer than {LastKnownSequenceNumber?.ToString() ?? "null"}. Ignoring.");
                    }
                }
                else
                {
                    Console.WriteLine($"[TorrentManager {LogName}] No update found or invalid data from DHT GetAsync (missing value/pk/sig/seq).");
                LastMutableUpdateCheckTimer.Restart ();
                }
                // else: No update found or immutable item received (which shouldn't happen for xs= links)

            } catch (Exception ex) {
                Console.WriteLine($"[TorrentManager {LogName}] Exception during PerformMutableUpdateCheckAsync: {ex.Message}");
                // Log the error appropriately
                Console.WriteLine($"[Error] BEP46 for {this.LogName}: Error during mutable update check: {ex.Message}");
            }
        }

        // Verifies the Ed25519 signature for a mutable torrent update (BEP44/BEP46)
        bool VerifyMutableSignature(BEncodedString publicKey, BEncodedString? salt, long sequenceNumber, BEncodedValue value, BEncodedString signature)
        {
             // Original logic restored
            try
            // Construct the data to verify according to BEP44: "salt" + salt + "seq" + seq + "v" + value
            {
            Console.WriteLine($"[TorrentManager {LogName}] VerifyMutableSignature: PK={publicKey.ToHex()}, Salt={salt?.ToHex() ?? "null"}, Seq={sequenceNumber}, Value={value.ToString()}, Sig={signature.ToHex()}");
                // Construct the data to verify according to BEP44: "salt" + salt + "seq" + seq + "v" + value
                // Calculate the total length needed first
                int saltKeyLength = new BEncodedString("salt").LengthInBytes();
                int seqKeyLength = new BEncodedString("seq").LengthInBytes();
                int vKeyLength = new BEncodedString("v").LengthInBytes();

                int saltLength = (salt == null || salt.Span.Length == 0) ? 0 : (saltKeyLength + salt.LengthInBytes());
                int seqLength = seqKeyLength + new BEncodedNumber(sequenceNumber).LengthInBytes();
                int valueLength = vKeyLength + value.LengthInBytes();
                int totalLength = saltLength + seqLength + valueLength;

                // Rent a buffer
                using var rented = System.Buffers.MemoryPool<byte>.Shared.Rent(totalLength);
                Span<byte> dataToVerify = rented.Memory.Span.Slice(0, totalLength);

                // Encode the parts into the buffer
                int offset = 0;
                if (saltLength > 0)
                {
                    offset += new BEncodedString("salt").Encode(dataToVerify.Slice(offset));
                    offset += salt!.Encode(dataToVerify.Slice(offset)); // salt is checked for null by saltLength > 0
                }
                offset += new BEncodedString("seq").Encode(dataToVerify.Slice(offset));
                offset += new BEncodedNumber(sequenceNumber).Encode(dataToVerify.Slice(offset));
                offset += new BEncodedString("v").Encode(dataToVerify.Slice(offset));
                offset += value.Encode(dataToVerify.Slice(offset));

                // Verify the signature using BouncyCastle
                var verifier = new Ed25519Signer();
                var publicKeyParams = new Ed25519PublicKeyParameters(publicKey.Span.ToArray());
                verifier.Init(false, publicKeyParams); // false for verification
                verifier.BlockUpdate(dataToVerify.ToArray(), 0, dataToVerify.Length); // Use the byte array overload

                bool isValid = verifier.VerifySignature(signature.Span.ToArray());

                if (!isValid)
                {
                    logger.ErrorFormatted("BEP46 for {0}: Signature verification failed. DataToVerify (hex): {1}", this.LogName, BitConverter.ToString(dataToVerify.ToArray()).Replace("-",""));
                    logger.ErrorFormatted("BEP46 for {0}: Signature verification failed.", this.LogName);
                }
                return isValid;
            }
            catch (Exception ex)
            {
                logger.Exception(ex, $"BEP46 for {this.LogName}: Exception during signature verification.");
                logger.Exception(ex, $"BEP46 for {this.LogName}: Exception during signature verification.");
                return false;
            }
        }
        #endregion

        // Helper needed in constructor
        private static byte[] HexDecode(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even number of characters");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        internal void SetTrackerManager (ITrackerManager manager)
        {
            if (TrackerManager != null) {
                TrackerManager.AnnounceComplete -= HandleTrackerAnnounceComplete;
            }

            TrackerManager = manager;

            if (TrackerManager != null) {
                TrackerManager.AnnounceComplete += HandleTrackerAnnounceComplete;
            }
        }

        async void HandleTrackerAnnounceComplete (object? o, AnnounceResponseEventArgs e)
        {
            if (e.Successful) {
                await ClientEngine.MainLoop;

                int count = 0;
                foreach (var kvp in e.Peers)
                    count += AddPeers (kvp.Value, prioritise: true, fromTracker: true);
                RaisePeersFound (new TrackerPeersAdded (this, count, e.Peers.Count, e.Tracker));

                if (Engine != null)
                    Engine.ConnectionManager.TryConnect ();
            }
        }

        #endregion Private Methods

        internal bool TrySetError (Reason reason, Exception ex)
        {
            if (Mode is ErrorMode)
                return false;

            Error = new Error (reason, ex);
            logger.Error ($"{this.LogName} {reason}: {ex.Message}");
            logger.Debug ($"{ex}");
            Mode = new ErrorMode (this, Engine!.ConnectionManager);
            return true;
        }

        IList<ITorrentManagerFile> IPieceRequesterData.Files => Files;
        int IPieceRequesterData.PieceCount => Torrent == null ? 0 : Torrent.PieceCount;
        int IPieceRequesterData.PieceLength => Torrent == null ? 0 : Torrent.PieceLength;
        int IPieceRequesterData.SegmentsPerPiece (int pieceIndex)
            => Torrent == null ? 0 : Torrent.BlocksPerPiece (pieceIndex);
        int IPieceRequesterData.ByteOffsetToPieceIndex (long byteOffset)
            => Torrent == null ? 0 : Torrent.ByteOffsetToPieceIndex (byteOffset);
        int IPieceRequesterData.BytesPerPiece (int piece)
            => Torrent == null ? 0 : Torrent.BytesPerPiece (piece);
        void IMessageEnqueuer.EnqueueRequest (IRequester peer, PieceSegment block)
            => ((IMessageEnqueuer) this).EnqueueRequests (peer, stackalloc PieceSegment[] { block });
        void IMessageEnqueuer.EnqueueRequests (IRequester peer, Span<PieceSegment> segments)
        {
            (var bundle, var releaser) = RequestBundle.Rent<RequestBundle> ();
            if (segments.Length < 32) {
                Span<BlockInfo> blocks = stackalloc BlockInfo[segments.Length];
                bundle.Initialize (segments.ToBlockInfo (blocks, this));
            } else {
                var blocks = new BlockInfo[segments.Length];
                bundle.Initialize (segments.ToBlockInfo (blocks, this));
            }

            ((PeerId) peer).MessageQueue.Enqueue (bundle, releaser);
        }

        void IMessageEnqueuer.EnqueueCancellation (IRequester peer, PieceSegment segment)
        {
            (var msg, var releaser) = PeerMessage.Rent<CancelMessage> ();
            var blockInfo = segment.ToBlockInfo (this);
            msg.Initialize (blockInfo.PieceIndex, blockInfo.StartOffset, blockInfo.RequestLength);
            ((PeerId) peer).MessageQueue.Enqueue (msg, releaser);
 
           }

      void IMessageEnqueuer.EnqueueCancellations (IRequester peer, Span<PieceSegment> segments)
        {
            for (int i = 0; i < segments.Length; i++)
                ((IMessageEnqueuer) this).EnqueueCancellation (peer, segments[i]);
        }
    }
}
