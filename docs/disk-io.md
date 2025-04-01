# Disk I/O and Caching

MonoTorrent employs a layered approach to manage reading and writing torrent data to the disk, incorporating an optional memory cache for performance optimization.

## Core Components

*   **`IPieceWriter` (`src/PieceWriter/IPieceWriter.cs`):** This is the fundamental interface defining operations for interacting with the file system at the torrent file level. It abstracts the underlying storage mechanism. Key methods include:
    *   `ReadAsync`: Reads a block of data from a specific file at a given offset.
    *   `WriteAsync`: Writes a block of data to a specific file at a given offset.
    *   `CloseAsync`: Closes any open file handles for a specific file.
    *   `CreateAsync`: Creates a file on disk, potentially pre-allocating space or creating a sparse file based on `FileCreationOptions`.
    *   `ExistsAsync`: Checks if a file exists.
    *   `FlushAsync`: Ensures any buffered data for a file is written to the physical disk.
    *   `GetLengthAsync`: Gets the current length of a file on disk.
    *   `MoveAsync`: Moves a file to a new location.
    *   `SetLengthAsync`: Sets the length of a file (used for pre-allocation).
    *   `SetMaximumOpenFilesAsync`: Configures the maximum number of concurrent file handles the writer should keep open.

*   **`DiskWriter` (`src/PieceWriter/DiskWriter.cs`):** The default implementation of `IPieceWriter`.
    *   **File Handling:** Manages `FileStream` objects for reading and writing.
    *   **File Limit:** Respects the `MaximumOpenFiles` setting, closing the least recently used file streams when the limit is reached to conserve resources. It uses a `ReusableSemaphore` to limit concurrent disk operations.
    *   **Pre-allocation/Sparse Files:** Attempts to use efficient file creation methods (pre-allocation via `SetLength` or NTFS sparse files via P/Invoke) based on the `FileCreationOptions` provided to `CreateAsync`. Falls back to creating standard empty files if specialized methods fail or are unavailable.

*   **`IBlockCache` (`src/PieceWriter/IBlockCache.cs`):** An interface defining a block-level cache that sits *in front* of an `IPieceWriter`. Its purpose is to reduce disk I/O by serving frequently accessed blocks directly from memory.
    *   **Methods:** Provides `ReadAsync` and `WriteAsync` methods similar to `IPieceWriter`, but operates on `BlockInfo` (piece index, offset, length) rather than file offsets. It also has `ReadFromCacheAsync` to attempt reading *only* from the cache.
    *   **Properties:** Exposes `Capacity`, `CacheUsed`, `CacheHits`, `CacheMisses`, and the current `CachePolicy`.
    *   **Events:** Raises events like `ReadFromCache`, `ReadThroughCache`, `WrittenToCache`, `WrittenThroughCache` to allow monitoring cache behavior.

*   **`MemoryCache` (`src/PieceWriter/MemoryCache.cs`):** The default implementation of `IBlockCache`.
    *   **Storage:** Uses a `Dictionary<ITorrentManagerInfo, List<CachedBlock>>` to store blocks in memory, renting buffers from a `MemoryPool`.
    *   **Capacity Management:** When the cache reaches its `Capacity`, it evicts blocks (currently seems to evict the first flushable block found, though LRU might be intended) to make space for new writes. Evicted blocks that were pending writes are flushed to the underlying `IPieceWriter`.
    *   **Cache Policy (`CachePolicy` enum):**
        *   `ReadsAndWrites`: Caches blocks on both read misses and writes.
        *   `WritesOnly`: Only caches blocks during write operations. Reads always go directly to the underlying `IPieceWriter`.
    *   **Write Strategy:** Writes can either go directly to the cache (and later flushed to disk upon eviction or explicit flush) or bypass the cache (`preferSkipCache` or if the block is larger than the cache capacity) and go directly to the `IPieceWriter`.
    *   **Read Strategy:** Attempts to read from the cache first. If the block is found (`CacheHit`), it's returned directly. If not (`CacheMiss`), it reads from the underlying `IPieceWriter`. If the policy is `ReadsAndWrites`, the block read from the writer is then added to the cache.

## Integration (`ClientEngine`, `DiskManager`)

*   The `ClientEngine` holds a single `DiskManager` instance (which seems to be the `MonoTorrent.Client.DiskManager` class, not the `MonoTorrent.PieceWriter.DiskWriter` directly, though it likely *uses* the latter internally).
*   The `ClientEngine`'s `DiskManager` is configured with an `IPieceWriter` (typically `DiskWriter`) and potentially an `IBlockCache` (typically `MemoryCache`) based on `EngineSettings`. The `Factories` system allows providing custom implementations.
*   `TorrentManager` instances interact with the `ClientEngine`'s `DiskManager` to perform all disk operations (reading for uploads/hashing, writing downloads). The `DiskManager` then routes these requests through the `IBlockCache` (if present) and finally to the `IPieceWriter`.

This layered approach allows for optimized disk access by caching frequently used blocks while abstracting the details of file handling and caching policies.