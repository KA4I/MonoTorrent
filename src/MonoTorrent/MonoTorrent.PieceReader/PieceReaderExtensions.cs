using System;

using ReusableTasks;

namespace MonoTorrent.BlockReader
{
    public static class PieceReaderExtensions
    {
        public static ReusableTask<int> ReadAsync (this IBlockReader reader, ITorrentManagerInfo torrent, BlockInfo request, Memory<byte> buffer)
        {
            if (reader is null)
                throw new ArgumentNullException (nameof(reader));
            if (torrent is null)
                throw new ArgumentNullException (nameof(torrent));
            if (request.RequestLength > buffer.Length)
                throw new ArgumentOutOfRangeException (nameof(buffer), "The buffer is not large enough to contain the requested data.");

            buffer = buffer.Slice (0, request.RequestLength);
            long offset = torrent.TorrentInfo!.PieceIndexToByteOffset (request.PieceIndex) + request.StartOffset;

            return reader.ReadAsync (torrent, offset, buffer);
        }
    }
}
