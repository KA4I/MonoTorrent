using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using MonoTorrent.BlockReader;

using ReusableTasks;

namespace MonoTorrent.PieceWriter
{
    public sealed class BlockBasedPieceWriter : IPieceWriter
    {
        readonly IBlockWriter writer;
        readonly IBlockReader reader;
        readonly IEnumerable<ITorrentManagerInfo> torrents;

        public BlockBasedPieceWriter (IBlockReader reader, IBlockWriter writer, IEnumerable<ITorrentManagerInfo> torrents)
        {
            this.reader = reader ?? throw new ArgumentNullException (nameof (reader));
            this.writer = writer ?? throw new ArgumentNullException (nameof (writer));
            this.torrents = torrents ?? throw new ArgumentNullException (nameof (torrents));
        }

        public ReusableTask<int> ReadAsync (ITorrentManagerFile file, long offset, Memory<byte> buffer)
        {
            ThrowIfNoSyncContext ();

            if (offset < 0 || checked(offset + buffer.Length) > file.Length)
                throw new ArgumentOutOfRangeException (
                    nameof (offset),
                    "The offset and buffer length must be within the bounds of the file."
                );

            return reader.ReadAsync (this.GetTorrent (file), offset + file.OffsetInTorrent, buffer);
        }

        public ReusableTask WriteAsync (ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
        {
            ThrowIfNoSyncContext ();

            if (offset < 0 || checked(offset + buffer.Length) > file.Length)
                throw new ArgumentOutOfRangeException (
                    nameof (offset),
                    "The offset and buffer length must be within the bounds of the file."
                );

            return writer.WriteAsync (this.GetTorrent (file), offset + file.OffsetInTorrent, buffer);
        }

        public ReusableTask<long?> GetLengthAsync (ITorrentManagerFile file)
            => ReusableTask.FromResult<long?> (file.Length);

        public ReusableTask<bool> SetLengthAsync(ITorrentManagerFile file, long length)
        {
            if (file.Length != length)
                throw new ArgumentOutOfRangeException (nameof(length), length, "All 'files' are preallocated");

            return ReusableTask.FromResult (true);
        }

        /// <inheritdoc/>
        public ReusableTask<bool> CreateAsync (ITorrentManagerFile file, FileCreationOptions options)
            => ReusableTask.FromResult (false);

        public ReusableTask<bool> ExistsAsync (ITorrentManagerFile file)
            => ReusableTask.FromResult (true);

        public ReusableTask FlushAsync (ITorrentManagerFile file)
            => writer.FlushAsync (this.GetTorrent (file));

        ITorrentManagerInfo GetTorrent (ITorrentManagerFile file)
        {
            foreach (var torrent in torrents)
                if (torrent.Files.Contains (file))
                    return torrent;
            throw new InvalidOperationException ("The file does not belong to any of the torrents.");
        }

        public int OpenFiles => 0;
        public int MaximumOpenFiles => 0;

        public ReusableTask CloseAsync (ITorrentManagerFile file)
            => this.FlushAsync (file);

        public ReusableTask MoveAsync (ITorrentManagerFile file, string fullPath, bool overwrite)
            => throw new NotSupportedException ();

        public ReusableTask SetMaximumOpenFilesAsync (int maximumOpenFiles)
            => ReusableTask.CompletedTask;

        [Conditional ("DEBUG")]
        void ThrowIfNoSyncContext ()
        {
            if (SynchronizationContext.Current is null)
                throw new InvalidOperationException ();
        }

        public void Dispose () => (this.writer as IDisposable)?.Dispose ();
    }
}
