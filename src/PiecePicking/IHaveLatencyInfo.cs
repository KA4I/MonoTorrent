using System.Diagnostics;

namespace MonoTorrent.PiecePicking
{
    public interface IHaveLatencyInfo
    {
        /// <summary>In <see cref="Stopwatch"/> ticks</summary>
        /// <seealso cref="Stopwatch.Frequency"/>
        int WorstLatency { get; }
    }
}
