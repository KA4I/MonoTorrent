using System.Collections.Generic;

namespace MonoTorrent;

class OrderComparer : IComparer<TorrentPath>
{
    public static OrderComparer Instance { get; } = new();

    public int Compare (TorrentPath x, TorrentPath y)
    {
        return x.CompareTo (y);
    }
}
