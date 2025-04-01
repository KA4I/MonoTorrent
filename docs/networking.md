# Networking

MonoTorrent handles various networking protocols and discovery mechanisms essential for BitTorrent operation.

## Peer Connections (`src/Connections/Peer/`)

This is the core of data transfer in BitTorrent.

*   **Connection Establishment:** The `ConnectionManager` (managed by `ClientEngine`) initiates outgoing connections to peers obtained from trackers, DHT, PEX, or LSD. The `ListenManager` accepts incoming connections on the configured endpoints (`EngineSettings.ListenEndPoints`).
*   **Protocol:** Peers communicate using the BitTorrent peer wire protocol. This involves:
    *   **Handshake:** Exchanging infohashes and peer IDs to confirm connection to the correct torrent and peer.
    *   **Message Loop:** Sending and receiving messages like `Bitfield`, `Have`, `Interested`, `Choke`, `Request`, `Piece`, `Cancel`, etc.
*   **Encryption:** Supports Message Stream Encryption (MSE) for obfuscating traffic (if `EngineSettings.AllowedEncryption` allows).
*   **Implementations:** Likely includes TCP-based connections (`SocketPeerConnection`). uTP (Micro Transport Protocol) might also be supported for UDP-based peer connections.
*   **Connection Pooling:** The `ConnectionManager` manages the pool of active connections, respecting global and per-torrent connection limits.

## Tracker Communication (`src/Trackers/`, `src/Connections/Tracker/`)

Trackers are central servers that help peers find each other for a given torrent.

*   **HTTP/HTTPS Trackers:** Uses `HttpTrackerConnection` (likely leveraging `HttpClient` from `HttpRequestFactory`) to send announce requests (reporting stats, requesting peers) via HTTP GET requests. Handles compact peer responses.
*   **UDP Trackers:** Uses `UdpTrackerConnection` for a more lightweight, connectionless announce mechanism over UDP. Handles connection IDs and specific UDP tracker protocol messages.
*   **`TrackerManager`:** (Likely within `src/Client/Managers/`) Manages the list of trackers for a `TorrentManager`, handling announce schedules, tiers, and responses.
*   **`TrackerFactory`:** (Part of `Factories`) Creates appropriate `ITracker` instances based on the tracker URL scheme (http, https, udp).

## Distributed Hash Table (DHT) (`src/Dht/`, `src/Connections/Dht/`)

DHT allows trackerless torrent operation by letting peers find each other directly on a distributed network.

*   **`IDhtEngine`:** The core DHT engine implementation (e.g., `DhtEngine`). Responsible for maintaining the routing table, performing node lookups, storing peer information, and responding to queries from other DHT nodes.
*   **`IDhtListener`:** Listens for incoming DHT messages on the configured UDP endpoint (`EngineSettings.DhtEndPoint`).
*   **Bootstrapping:** Uses initial nodes (from the torrent file's `nodes` key or known routers) to join the DHT network.
*   **Operations:** Performs `announce` (stores its infohash/port) and `get_peers` (requests peers for an infohash) operations on the DHT.

## Local Peer Discovery (LSD) (`src/Client/LocalPeerDiscovery.cs`)

Finds peers for torrents on the local network using UDP multicast messages, reducing reliance on external trackers or DHT for local transfers.

*   **`ILocalPeerDiscovery`:** Interface for the LSD service.
*   **Operation:** Periodically sends multicast messages announcing the infohashes it's interested in and listens for responses from other local clients.
*   **Integration:** Discovered peers are passed to the relevant `TorrentManager` via the `ClientEngine`.

## Port Forwarding (`src/PortForwarding/`, `Mono.Nat`)

Attempts to automatically configure the user's router to forward the necessary ports for incoming connections, improving connectability.

*   **`IPortForwarder`:** Interface for port forwarding services.
*   **`MonoNatPortForwarder`:** Default implementation using the Mono.Nat library, which supports UPnP and NAT-PMP protocols.
*   **Operation:** Detects compatible routers on the network and attempts to create mappings for the TCP peer listening ports and the UDP DHT port. Mappings are typically refreshed periodically.
*   **`Mappings`:** Represents the currently active port mappings.