# Client Engine & Peer Management

This section describes the components responsible for managing the download/upload process, interacting with peers, and tracking torrent state within the MonoTorrent library.

## `ClientEngine` (`src/Client/ClientEngine.cs`)

The `ClientEngine` is the central orchestrator for all BitTorrent operations. It acts as the main entry point for managing torrents and global client settings.

**Responsibilities:**

*   **Initialization:** Created with `EngineSettings` (configuring limits, ports, directories, features) and `Factories` (allowing customization of core components). It generates a unique `PeerId` for the client session.
*   **Torrent Management:** Maintains a list of active `TorrentManager` instances (`Torrents` property). Provides methods like `AddAsync`, `AddStreamingAsync`, and `RemoveAsync` to register, configure, and unregister torrents. It differentiates between publicly registered torrents and internal ones (e.g., for metadata downloads).
*   **Service Coordination:** Manages the lifecycle (start/stop) of essential background services:
    *   **DHT Engine (`IDht`):** For trackerless peer discovery. Manages DHT nodes and responds to peer lookups.
    *   **Local Peer Discovery (`ILocalPeerDiscovery`):** Finds peers on the local network.
    *   **Peer Listeners (`IPeerConnectionListener`):** Listens on specified endpoints (`EngineSettings.ListenEndPoints`) for incoming peer connections via the `ListenManager`.
    *   **Port Forwarding (`IPortForwarder`):** Interacts with UPnP/NAT-PMP devices to automatically map listening ports if `EngineSettings.AllowPortForwarding` is enabled.
*   **Connection Management:** Owns the `ConnectionManager`, which handles outgoing peer connection attempts and manages the pool of active peer connections across all torrents.
*   **Disk Management:** Owns the `DiskManager`, which handles all read/write operations to the disk via an `IPieceWriter` and manages the disk cache.
*   **Global Rate Limiting:** Enforces overall maximum upload and download rates (`EngineSettings.MaximumUploadRate`, `EngineSettings.MaximumDownloadRate`) using internal `RateLimiter` instances, which are shared across all torrent managers.
*   **State Persistence:** Offers `SaveStateAsync` and `RestoreStateAsync` to serialize/deserialize the engine's settings and the state of registered torrents (including their settings, save paths, file priorities, and magnet links/metadata paths) to/from a file or byte array, allowing sessions to be resumed.
*   **Metadata Download:** Provides `DownloadMetadataAsync` to fetch torrent metadata (`.torrent` file content) directly from a `MagnetLink` using the peer network (DHT/PEX).
*   **Main Loop Integration:** Uses the static `ClientEngine.MainLoop` for queuing all core logic, ensuring thread safety for engine and torrent manager operations.
*   **Events:** Exposes events like `StatsUpdate` and `CriticalException`.

## `TorrentManager` (`src/Client/Managers/TorrentManager.cs`)

Manages the lifecycle, state, and operations for a single torrent. Each torrent added to the `ClientEngine` gets its own `TorrentManager`.

**Responsibilities:**

*   **State Machine (`TorrentState`):** Controls the torrent's current status. Key transitions include:
    *   `Stopped` -> `Hashing`: When starting, verifies existing files against piece hashes.
    *   `Hashing` -> `Downloading` (or `Metadata` if from MagnetLink): After hashing or if no local data exists.
    *   `Metadata` -> `Hashing`/`Downloading`: Once `.torrent` metadata is retrieved.
    *   `Downloading` -> `Seeding`: When all pieces are downloaded and verified.
    *   `Downloading`/`Seeding` -> `Paused`: User pauses the torrent.
    *   `Downloading`/`Seeding` -> `Stopped`: User stops the torrent (requires final tracker announce).
    *   Any State -> `Error`: If a critical error occurs.
*   **Peer Management:** Maintains a list of peers specific to this torrent, adding peers discovered via trackers, DHT, PEX, or LSD. Manages connections to these peers via the `ClientEngine.ConnectionManager`.
*   **Piece Selection:** Uses an `IPieceRequester` (e.g., `StandardPieceRequester`, `StreamingPieceRequester`) to determine which pieces to request from connected peers based on availability and strategy (rarest first, sequential, etc.).
*   **Data Transfer:** Coordinates with the `DiskManager` for reading pieces to upload and writing downloaded pieces. Interacts with `PeerIO` (likely within `src/Client/Connections/`) to send/receive BitTorrent protocol messages (Have, Bitfield, Request, Piece, Cancel, etc.).
*   **Tracker Communication:** Manages communication with the trackers listed in the torrent's `AnnounceUrls` to report progress and get more peers.
*   **Hashing/Verification:** Initiates hashing of local files on startup and verifies downloaded pieces against the `Torrent.PieceHashes`.
*   **Settings:** Holds torrent-specific settings (`TorrentSettings`) which can override engine-wide defaults (e.g., max connections per torrent).
*   **Statistics:** Tracks download/upload rates (`Monitor`), progress (`Progress`), and other torrent-specific metrics.

## Peer Representation (`src/PeerInfo.cs`, `src/PeerID.cs`)

*   **`PeerInfo`:** Represents a potential or connected peer, storing its connection URI (`ConnectionUri`) and optionally its Peer ID (`PeerId`). Includes methods for compact peer representation (used in tracker/DHT communication).
*   **`PeerID.cs` (`Software` struct):** Contains logic to parse Peer IDs and attempt to identify the BitTorrent client software used by the peer based on common conventions (e.g., `-TR2940-` for Transmission 2.94).

## Torrent State (`src/Enums.cs`)

The `TorrentState` enum defines the possible states a `TorrentManager` can be in:

*   `Stopped`: Inactive, no connections, no hashing.
*   `Paused`: Temporarily inactive, connections dropped, but progress is saved.
*   `Starting`: Transition state before hashing or downloading.
*   `Downloading`: Actively downloading pieces, connecting to peers.
*   `Seeding`: Download complete (100%), actively uploading to peers.
*   `Hashing`: Verifying local files against piece hashes.
*   `HashingPaused`: Hashing process has been paused.
*   `Stopping`: Transition state while performing final announces before stopping.
*   `Error`: A fatal error occurred (e.g., disk error, invalid metadata).
*   `Metadata`: Downloading `.torrent` metadata from peers (usually via Magnet Link).
*   `FetchingHashes`: Downloading V2 piece layers/hashes from peers.