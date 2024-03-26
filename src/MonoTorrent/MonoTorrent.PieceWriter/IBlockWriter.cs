using System;

using ReusableTasks;

namespace MonoTorrent.PieceWriter
{
    public interface IBlockWriter
    {
        ReusableTask WriteAsync (ITorrentManagerInfo torrent, long offset, ReadOnlyMemory<byte> buffer);
        ReusableTask FlushAsync (ITorrentManagerInfo torrent);
    }
}
