using System;
using System.Collections.Generic;
using System.IO;

using ReusableTasks;

namespace MonoTorrent.PieceWriter
{
    public class StreamBlockWriter : IBlockWriter
    {
        readonly Dictionary<ITorrentManagerInfo, Stream> torrents = new Dictionary<ITorrentManagerInfo, Stream> ();
        readonly Func<ITorrentManagerInfo, ReusableTask<Stream>> streamFactory;

        public StreamBlockWriter (Func<ITorrentManagerInfo, ReusableTask<Stream>> streamFactory)
        {
            this.streamFactory = streamFactory ?? throw new ArgumentNullException (nameof (streamFactory));
        }

        public async ReusableTask WriteAsync (ITorrentManagerInfo torrent, long offset, ReadOnlyMemory<byte> buffer)
        {
            if (torrent is null)
                throw new ArgumentNullException (nameof (torrent));

            if (offset < 0 || checked(offset + buffer.Length) > torrent.TorrentInfo!.Size)
                throw new ArgumentOutOfRangeException (
                    nameof (offset),
                    "The offset and buffer length must be within the bounds of the torrent."
                );

            if (!torrents.TryGetValue (torrent, out var stream)) {
                stream = await streamFactory (torrent).ConfigureAwait (false);
                torrents[torrent] = stream;
            }

            stream.Seek (offset, SeekOrigin.Begin);
            using var _ = MemoryPool.Default.Rent (buffer.Length, out ArraySegment<byte> segment);
            buffer.CopyTo (segment);
            await stream.WriteAsync (segment.Array, segment.Offset, segment.Count).ConfigureAwait (false);
        }

        public async ReusableTask FlushAsync (ITorrentManagerInfo torrent)
        {
            if (torrent is null)
                throw new ArgumentNullException (nameof (torrent));

            if (!torrents.TryGetValue (torrent, out var stream))
                return;

            await stream.FlushAsync ().ConfigureAwait (false);
        }
    }
}
