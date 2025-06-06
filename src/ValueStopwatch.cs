﻿//
// ValueStopwatch.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2019 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Diagnostics;
using System.Threading;

namespace MonoTorrent
{
    internal struct ValueStopwatch
    {
        const long NotStarted = 0;

        public static ValueStopwatch StartNew()
        {
            return new ValueStopwatch { _startedAt = Stopwatch.GetTimestamp() };
        }

        public static ValueStopwatch WithTime(TimeSpan time)
        {
            var offset = Stopwatch.Frequency == TimeSpan.TicksPerSecond
                ? time.Ticks
                : time.Ticks * ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
            return new ValueStopwatch
            {
                _startedAt = Stopwatch.GetTimestamp() - (long)offset
            };
        }

        long _elapsed;
        long _startedAt;

        long elapsed
        {
            get => Interlocked.Read (ref _elapsed);
            set => Interlocked.Exchange (ref _elapsed, value);
        }

        long startedAt
        {
            get => Interlocked.Read (ref _startedAt);
            set => Interlocked.Exchange (ref _startedAt, value);
        }

        public TimeSpan Elapsed
        {
            get
            {
                long totalElapsed = elapsed;
                if (IsRunning)
                    totalElapsed += Stopwatch.GetTimestamp() - startedAt;

                return Stopwatch.Frequency == TimeSpan.TicksPerSecond
                     ? TimeSpan.FromTicks(totalElapsed)
                     : TimeSpan.FromTicks((long)(totalElapsed / ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond)));
            }
        }

        public long ElapsedMilliseconds => (long)Elapsed.TotalMilliseconds;

        public long ElapsedTicks => Elapsed.Ticks;

        public bool IsRunning => startedAt != NotStarted;

        public void Reset()
        {
            elapsed = 0;
            startedAt = NotStarted;
        }

        public void Restart()
        {
            elapsed = 0;
            startedAt = Stopwatch.GetTimestamp();
        }

        public void Start()
        {
            Interlocked.CompareExchange (ref _startedAt, Stopwatch.GetTimestamp (), NotStarted);
        }

        public void Stop()
        {
            if (IsRunning)
            {
                Interlocked.Add (ref _elapsed, Stopwatch.GetTimestamp() - startedAt);
                startedAt = NotStarted;
            }
        }
    }
}
