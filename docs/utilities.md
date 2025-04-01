# Utilities and Data Structures

MonoTorrent utilizes several specialized data structures and utility classes for efficiency and core functionality.

## Bitfields (`src/BitField.cs`, `src/BitFieldData.cs`, `src/ReadOnlyBitField.cs`)

A `BitField` is used to efficiently track the availability of pieces within a torrent. Each bit corresponds to a piece index (0 to Length-1). It's crucial for peers to communicate which pieces they have.

*   **Efficiency:** Internally uses a `BitFieldData` class which stores the bits in a `ulong[]` array for compact storage and fast bitwise operations. It tracks the `TrueCount` for quick completion checks.
*   **Constructors:** Can be created with a specific length (all initialized to false), from a `bool[]`, from a `ReadOnlySpan<byte>` (representing the compact BitTorrent protocol format), or by cloning another `BitField` or `ReadOnlyBitField`.
*   **Operations:**
    *   **Indexing (`this[index]`):** Get or set the value of a specific bit (piece). Setting a bit updates the `TrueCount`.
    *   **Logical Operations:** `And`, `Or`, `Xor`, `NAnd`, `Not` methods perform bitwise operations between this bitfield and another (`ReadOnlyBitField`), modifying this instance.
    *   **Searching:** `FirstTrue()` and `FirstFalse()` (and their ranged overloads) efficiently find the index of the first piece with the desired state.
    *   **Bulk Set:** `SetAll(bool)`, `SetTrue(params int[])`, `SetFalse(params int[])`, `SetTrue((start, end))` allow setting multiple bits efficiently.
    *   **Serialization:** `ToBytes()` converts the bitfield to its byte representation for sending over the network. `From(ReadOnlySpan<byte>)` populates the bitfield from received data.
*   **Mutability & Immutability:**
    *   `BitField` is mutable and provides methods to change its state. It implicitly converts to `ReadOnlyBitField`.
    *   `ReadOnlyBitField` provides an immutable view of a bitfield's data, suitable for sharing without allowing modification.
*   **Iteration:** `TrueIndices` and `FalseIndices` properties return `IEnumerable<int>` structures (`BitFieldIndices`) that efficiently iterate over the indices of set or unset bits, respectively, using a custom `BitFieldScanner` enumerator.

## Buffer Management (`src/ByteBufferPool.cs`, `src/MemoryPool.cs`)

To minimize memory allocations and garbage collection pressure during network operations (especially socket reads/writes), MonoTorrent uses buffer pooling.

*   **`ByteBufferPool`:** Abstract base class defining the pooling logic. It maintains internal pools (using `SpinLocked<Stack/Queue>`) for different buffer sizes.
    *   `SmallMessageBufferSize` (256 bytes)
    *   `LargeMessageBufferSize` (BlockSize + 32 bytes, typically ~16KB)
    *   `MassiveBuffers` (for sizes larger than LargeMessageBufferSize)
*   **`MemoryPool`:** The concrete implementation used by default (`MemoryPool.Default`).
*   **Renting Buffers:** The `Rent(int capacity, out Memory<byte>)` (or `out ArraySegment<byte>`) method retrieves a buffer of at least the requested `capacity` from the appropriate pool. If no suitable buffer is available, a new one might be allocated. The returned `Memory<byte>` is sliced to the exact requested capacity.
*   **Returning Buffers:** Renting returns an `IDisposable` `Releaser` struct. Disposing the `Releaser` returns the buffer to the correct pool, making it available for reuse. This pattern ensures buffers are always returned. Double-disposing a `Releaser` throws an exception.
*   **Pinned Buffers:** On newer .NET runtimes (.NET 5+), the pool attempts to allocate pinned buffers (`GC.AllocateUninitializedArray<byte>(pinned: true)`) to reduce heap fragmentation caused by pinning during async socket operations.

## Concurrency (`src/MainLoop.cs`, `src/Semaphore*.cs`, `src/SimpleSpinLock.cs`, `src/ThreadSwitcher.cs`)

Provides tools for managing asynchronous operations and ensuring thread safety, particularly for accessing shared state within the `ClientEngine` and `TorrentManager`.

*   **`MainLoop`:** A core component that runs a dedicated background thread.
    *   **`SynchronizationContext`:** It acts as a `SynchronizationContext`, allowing `async/await` operations started on the main loop to resume on the same thread automatically.
    *   **Queuing:** Provides methods to queue work onto its thread:
        *   `Post(callback, state)`: Asynchronously queues a `SendOrPostCallback`.
        *   `Send(callback, state)`: Synchronously executes a `SendOrPostCallback` if called from the main loop thread, otherwise queues it and blocks the caller until execution completes. `QueueWait(Action)` is a convenience wrapper.
        *   `QueueTimeout(TimeSpan, Func<bool>)`: Schedules a recurring task to run on the main loop.
    *   **Awaitable:** The `MainLoop` instance itself is awaitable (`await ClientEngine.MainLoop;`). Awaiting it ensures the code following the `await` runs on the main loop thread.
    *   **Thread Switching:** Provides static methods `SwitchToThreadpool()` and `SwitchThread()` returning awaitable structs (`EnsureThreadPool`, `ThreadSwitcher`) to transition execution *off* the main loop and onto the thread pool.
*   **`SemaphoreLocked<T>`, `SpinLocked<T>`:** Lightweight locking mechanisms for protecting shared data `T`.
    *   `Enter(out T value)` or `EnterAsync()`: Acquire the lock and get access to the protected value. Returns an `IDisposable` `Releaser` struct.
    *   `Releaser.Dispose()`: Releases the lock. The `using` statement is the typical way to ensure release. `SemaphoreLocked` uses `ReusableSemaphore` internally, while `SpinLocked` uses `Interlocked.CompareExchange` with spinning/sleeping.
*   **`ReusableSemaphore`:** A custom semaphore implementation optimized for `ReusableTask` to reduce allocations compared to `SemaphoreSlim`.

## Hashing (`src/IPieceHashes.cs`, `src/PieceHashes*.cs`, `src/Merkle*.cs`)

Handles piece verification using SHA1 (V1) and Merkle Trees (V2).

*   **`IPieceHashes`:** Interface for accessing piece hash information for a torrent.
*   **`PieceHashesV1`:** Stores and validates the linear SHA1 hashes for V1 torrents from the `pieces` key in the info dictionary.
*   **`PieceHashesV2`:** Stores and validates V2 Merkle Tree roots (`PiecesRoot` per file) and layers (`piece layers` dictionary). Allows requesting specific hashes and proofs for verification using `TryGetV2Hashes`.
*   **`MerkleTree`, `MerkleTreeHasher`:** Classes responsible for building Merkle Trees from piece layer hashes and hashing data blocks/layers according to BEP52, including handling necessary padding hashes (`PaddingHashesByLayer`).

## Other Utilities

*   **`BigEndianBigInteger.cs`:** A wrapper around `System.Numerics.BigInteger` to handle big-endian byte order required by some cryptographic operations (like Diffie-Hellman key exchange).
*   **`Check.cs`:** Static helper class for concise null-checking of arguments (e.g., `Check.Stream(stream)`).
*   **`Factories.cs`:** Centralized factory (`Factories.Default` or custom instance) for creating instances of core interfaces (like `IPieceWriter`, `ITracker`, `IDhtEngine`, `IPeerConnection`), allowing for customization and dependency injection.
*   **`MemoryExtensions.cs`, `SpanExtensions.cs`, etc.:** Provide compatibility shims and helper methods for working with modern memory types (`Memory<T>`, `Span<T>`) and other common operations across different .NET target frameworks (e.g., `Stream.ReadAsync(Memory<byte>)`, `RandomNumberGenerator.GetBytes(Span<byte>)`).