# Torrent Handling

This section covers the core classes responsible for representing and manipulating torrent metadata within MonoTorrent.

## `Torrent` Class (`src/Torrent.cs`)

The `Torrent` class is the primary representation of a loaded torrent file (`.torrent`). It parses the BEncoded metadata from various sources and provides access to essential information.

### Loading Torrents

The `Torrent` class offers several static methods for loading torrent data:

*   **`Load(string path)`:** Loads a torrent from a local file path.
*   **`Load(Stream stream)`:** Loads a torrent from a stream containing the BEncoded data.
*   **`Load(BEncodedDictionary dictionary)`:** Loads a torrent directly from a pre-parsed BEncoded dictionary.
*   **`Load(ReadOnlySpan<byte> span)`:** Loads a torrent from raw BEncoded data in a byte span.
*   **`LoadAsync(...)`:** Asynchronous versions of the above methods.
*   **`LoadAsync(HttpClient client, Uri url, string savePath)`:** Downloads a torrent file from a URL using the provided `HttpClient` and saves it locally before loading.
*   **`TryLoad(...)`:** Non-throwing versions of the loading methods that return `true` and provide the loaded `Torrent` via an `out` parameter on success, or `false` on failure (e.g., invalid format, file not found).

Internally, these methods decode the BEncoded data, calculate the V1 and/or V2 infohashes, and parse the top-level dictionary and the crucial `info` dictionary.

### Key Properties

Once loaded, the `Torrent` object exposes the following properties:

*   **`InfoHashes` (`InfoHashes`):** Contains the V1 (SHA1) and/or V2 (SHA256) infohash(es) identifying the torrent. This is the primary identifier.
*   **`Name` (`string`):** The suggested name for the torrent, typically the root directory name for multi-file torrents or the filename for single-file torrents.
*   **`Files` (`IList<ITorrentFile>`):** A list of files included in the torrent. Each `ITorrentFile` provides details like path, length, and piece mapping.
*   **`PieceLength` (`int`):** The size of each data piece in bytes (except potentially the last one).
*   **`PieceCount` (`int`):** The total number of pieces in the torrent.
*   **`Size` (`long`):** The total size of all files in the torrent (including padding for V1/Hybrid).
*   **`AnnounceUrls` (`IList<IList<string>>`):** A list of tracker tiers, where each tier is a list of tracker URLs. Populated from the `announce` and `announce-list` keys.
*   **`Comment` (`string`):** An optional comment embedded in the torrent.
*   **`CreatedBy` (`string`):** An optional string indicating the software used to create the torrent.
*   **`CreationDate` (`DateTime`):** The date and time the torrent file was created.
*   **`Encoding` (`string`):** The text encoding specified in the torrent (often UTF-8).
*   **`IsPrivate` (`bool`):** Indicates if the torrent is marked as private, which restricts peer discovery methods like DHT and PEX.
*   **`Nodes` (`BEncodedList`):** A list of DHT bootstrap nodes specified in the torrent.
*   **`HttpSeeds` (`IList<Uri>`):** A list of URLs for HTTP/Web Seeds (GetRight style).
*   **`Publisher`, `PublisherUrl` (`string`):** Optional fields for publisher information.
*   **`Source` (`string`):** Optional field indicating the source of the torrent.
*   **`ED2K`, `SHA1` (`ReadOnlyMemory<byte>`):** Optional alternative file hashes sometimes included.

### Internal Processing

*   The constructor calls `LoadInternal` which iterates through the main torrent dictionary.
*   The `ProcessInfo` method specifically handles the `info` dictionary, extracting file lists (`files` or `file tree`), piece length, V1 piece hashes (`pieces`), and determining if it's a V1, V2, or Hybrid torrent based on the presence of keys like `pieces`, `meta version`, and `file tree`. It performs consistency checks for hybrid torrents.
*   The `CreatePieceHashes()` method provides access to an `IPieceHashes` object, which allows validating downloaded pieces against the stored V1 hashes or V2 Merkle Tree layers.

## `TorrentFile` Class (`src/TorrentFile.cs`)

Represents a single file within a multi-file torrent or the sole file in a single-file torrent.

*   **Path:** `Path` property (`TorrentPath`) stores the relative path within the torrent, composed of potentially multiple parts.
*   **Size:** `Length` property indicates the file size in bytes.
*   **Piece Mapping:** `StartPieceIndex` and `EndPieceIndex` map the file's data to the torrent's pieces. `PieceCount` is derived from these.
*   **Offset:** `OffsetInTorrent` specifies the file's starting position within the overall torrent data stream.
*   **V2 Hashing:** `PiecesRoot` holds the Merkle Root hash for this specific file in V2 torrents.
*   **Padding:** `Padding` property stores the number of padding bytes added *after* this file in V1/Hybrid torrents to ensure the *next* file aligns with a piece boundary (BEP 47).
*   **Attributes:** `Attributes` property (`TorrentFileAttributes` enum) indicates if the file is hidden, executable, a symlink, or a padding file.

## `InfoHash` and `InfoHashes` (`src/InfoHash.cs`, `src/InfoHashes.cs`)

These classes manage the unique identifiers for torrents.

*   **`InfoHash`:** Represents a single hash (either 20-byte SHA1 for V1 or 32-byte SHA256 for V2). Provides methods for creation from various formats (bytes, hex, base32) and conversion back to these formats. Implements `IEquatable<InfoHash>`.
*   **`InfoHashes`:** Holds both the V1 (`InfoHash? V1`) and V2 (`InfoHash? V2`) hashes for a torrent. It determines the `TorrentProtocol` (V1, V2, Hybrid) based on which hashes are present. The `V1OrV2` property provides the primary hash used for identification (preferring V1 in hybrid scenarios). Implements `IEquatable<InfoHashes>`.

## `MagnetLink` Class (`src/MagnetLink.cs`)

Parses and represents `magnet:` URIs. It extracts key information like:

*   Infohashes (`xt=urn:btih:` or `xt=urn:btmh:`) -> `InfoHashes` property.
*   Display Name (`dn=`) -> `Name` property.
*   Tracker URLs (`tr=`) -> `AnnounceUrls` list.
*   Web Seeds (`as=` or `ws=`) -> `Webseeds` list.
*   Exact Length (`xl=`) -> `Size` property.
*   Mutable Torrent info (`xs=urn:btpk:`, `s=`) -> `PublicKeyHex`, `SaltHex` properties (BEP46).

This allows initiating downloads without needing the full `.torrent` file initially, relying on mechanisms like DHT or PEX to find peers and potentially download the metadata.