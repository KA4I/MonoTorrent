using System;
using System.Diagnostics;
using System.Linq;

namespace MonoTorrent
{
    public sealed class LatencyMonitor
    {
        int index;
        readonly int[] latencies;

        public LatencyMonitor (int trackingCount)
        {
            if (trackingCount <= 0)
                throw new ArgumentOutOfRangeException (nameof(trackingCount));

            latencies = new int[trackingCount];
        }

        public void Add (int latency)
        {
            latencies[index] = latency;
            index = (index + 1) % latencies.Length;
        }

        public void Reset () => latencies.AsSpan ().Fill (checked((int) Stopwatch.Frequency));

        public int Worst => latencies.Max ();

        public int TrackingCount => latencies.Length;
    }
}
