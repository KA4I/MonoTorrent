using System;
using System.Collections.Generic;
using System.Diagnostics;

using C5;

namespace MonoTorrent.Client
{
    public class PeerMonitor : ConnectionMonitor
    {
        readonly Dictionary<RequestKey, IPriorityQueueHandle<long>> requestStarts = new Dictionary<RequestKey, IPriorityQueueHandle<long>> ();
        readonly IntervalHeap<long> requestTimes = new IntervalHeap<long> ();
        internal LatencyMonitor Latency { get; } = new LatencyMonitor (20);

        public void TrackRequest (int pieceIndex, int startOffset)
        {
            var request = new RequestKey { Piece = pieceIndex, Offset = startOffset };
            lock (requestStarts) {
                if (requestStarts.ContainsKey (request))
                    return;
                long timestamp = Stopwatch.GetTimestamp ();
                IPriorityQueueHandle<long>? handle = null;
                requestTimes.Add (ref handle!, timestamp);
                requestStarts.Add (request, handle);

            }
        }

        public void TrackResponse (int pieceIndex, int startOffset)
        {
            var request = new RequestKey { Piece = pieceIndex, Offset = startOffset };
            lock (requestStarts) {
                if (requestStarts.TryGetValue (request, out var startHandle)) {
                    long start = requestTimes.Delete (startHandle);
                    long now = Stopwatch.GetTimestamp ();
                    int latency = (int) (now - start);
                    int unfinishedLatency = 0;
                    if (requestTimes.Count > 0)
                        unfinishedLatency = (int) (now - requestTimes.FindMin());
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
