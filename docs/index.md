# MonoTorrent Library

Welcome to the documentation for the MonoTorrent library.

MonoTorrent is a cross-platform, open-source BitTorrent library written in C#. It provides functionalities for both downloading and creating torrents, supporting various BitTorrent features including V1, V2, and Hybrid torrents, DHT, Peer Exchange (PEX), Local Peer Discovery (LSD), and more.

## Core Components

*   **Torrent Handling:** Classes for loading, parsing, creating, and editing torrent metadata (`Torrent`, `TorrentCreator`, `TorrentEditor`).
*   **Client Engine:** Manages the overall download/upload process, peer connections, and torrent states (`ClientEngine`, `TorrentManager`).
*   **Networking:** Handles connections to peers, trackers, and the DHT network.
*   **Data Management:** Includes piece/block handling, disk I/O (`PieceWriter`, `DiskManager`), and caching (`MemoryCache`).

Navigate through the table of contents to explore the API reference and conceptual documentation.