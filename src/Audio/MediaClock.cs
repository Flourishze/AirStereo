using System;
using System.Diagnostics;

namespace AirStereo.Audio
{
    /// <summary>
    /// Monotonic media clock. The wall clock epoch is captured once at startup and every
    /// later reading is driven by the high resolution performance counter, so the value
    /// never jumps when the system clock is corrected.
    /// </summary>
    public sealed class MediaClock
    {
        private readonly Stopwatch origin = Stopwatch.StartNew();
        private readonly ulong epochNanoseconds;

        public MediaClock()
        {
            epochNanoseconds = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        }

        public ulong NowNanoseconds
        {
            get { return epochNanoseconds + (ulong)(origin.ElapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency)); }
        }
    }
}
