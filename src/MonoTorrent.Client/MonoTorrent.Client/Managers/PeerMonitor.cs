using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MonoTorrent.Client
{
    public class PeerMonitor : ConnectionMonitor
    {
        readonly Dictionary<RequestKey, long> requestStarts = new Dictionary<RequestKey, long> ();
        readonly SortedList<long, RequestKey> requestTimes = new SortedList<long, RequestKey> ();
        internal LatencyMonitor Latency { get; } = new LatencyMonitor (20);

        public void TrackRequest (int pieceIndex, int startOffset)
        {
            var request = new RequestKey { Piece = pieceIndex, Offset = startOffset };
            lock (requestStarts) {
                if (requestStarts.ContainsKey (request))
                    return;
                long timestamp = Stopwatch.GetTimestamp ();
                requestStarts.Add (request, timestamp);
                requestTimes.Add (timestamp, request);
            }
        }

        public void TrackResponse (int pieceIndex, int startOffset)
        {
            var request = new RequestKey { Piece = pieceIndex, Offset = startOffset };
            lock (requestStarts) {
                if (requestStarts.TryGetValue (request, out long timestamp)) {
                    requestTimes.Remove (timestamp);
                    long now = Stopwatch.GetTimestamp ();
                    int latency = (int) (now - timestamp);
                    int unfinishedLatency = 0;
                    if (requestTimes.Count > 0)
                        unfinishedLatency = (int) (now - requestTimes.Keys[0]);
                    Latency.Add (Math.Max (latency, unfinishedLatency));
                    requestStarts.Remove (request);
                }
            }
        }

        protected internal override void Reset ()
        {
            base.Reset ();
            Latency.Reset ();
        }

        struct RequestKey : IEquatable<RequestKey>
        {
            public int Piece, Offset;

            public override int GetHashCode () => Piece + Offset;

            public bool Equals (RequestKey other) => Piece == other.Piece && Offset == other.Offset;

            public override bool Equals (object? obj) => obj is RequestKey other && Equals (other);
        }
    }
}
