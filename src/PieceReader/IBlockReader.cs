using System;

using ReusableTasks;

namespace MonoTorrent.BlockReader
{
    public interface IBlockReader
    {
        ReusableTask<int> ReadAsync (ITorrentManagerInfo torrent, long offset, Memory<byte> buffer);
    }
}
