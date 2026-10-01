using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirStereo.Audio
{
    /// <summary>
    /// Sub-millisecond pacing. Thread.Sleep is bound to the system timer, which on Windows
    /// ticks every 15.6 ms until something raises the resolution, so a loop that asks to sleep
    /// "1 ms" can really be late by a whole audio block. A low buffer cannot absorb that, so
    /// the coarse part of the wait goes to a waitable timer created with
    /// CREATE_WAITABLE_TIMER_HIGH_RESOLUTION and the last fraction of a millisecond is spun.
    /// </summary>
    public static class PrecisionWait
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint Infinite = 0xFFFFFFFF;

        /// <summary>The timer is good to about a millisecond; everything below that is spun.</summary>
        private const double SpinMilliseconds = 0.8;

        // One timer per thread, created on first use and left to the process exit. The handle
        // cannot be shared: two threads waiting on the same auto reset timer would steal each
        // other's signal.
        [ThreadStatic] private static IntPtr timer;
        [ThreadStatic] private static bool timerProbed;
        private static bool raisedResolution;

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern int TimeBeginPeriod(int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name,
            uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr handle, ref long dueTime, int period,
            IntPtr completionRoutine, IntPtr argument, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        /// <summary>True when this thread got a high resolution timer, false when it fell back.</summary>
        public static bool HighResolution
        {
            get { return Handle() != IntPtr.Zero; }
        }

        private static IntPtr Handle()
        {
            if (timer == IntPtr.Zero && !timerProbed)
            {
                timerProbed = true;
                try
                {
                    timer = CreateWaitableTimerExW(IntPtr.Zero, null,
                        CreateWaitableTimerHighResolution, TimerAllAccess);
                }
                catch (Exception)
                {
                    // Older Windows builds have no high resolution timer; the plain sleep path
                    // below is still correct, it is only less accurate.
                    timer = IntPtr.Zero;
                }
                if (timer == IntPtr.Zero && !raisedResolution)
                {
                    // Without the high resolution timer, Thread.Sleep is quantised to the system
                    // tick, which is 15.6 ms - longer than two whole audio packets. Asking for a
                    // one millisecond tick is what every audio program on Windows does.
                    raisedResolution = true;
                    try { TimeBeginPeriod(1); }
                    catch (Exception) { }
                }
            }
            return timer;
        }

        /// <summary>
        /// Waits for a fractional number of milliseconds. Accuracy is well under a millisecond
        /// when the high resolution timer is available, and the 15.6 ms system tick otherwise.
        /// </summary>
        public static void Sleep(double milliseconds)
        {
            if (milliseconds <= 0.0) return;

            IntPtr handle = Handle();
            if (handle == IntPtr.Zero)
            {
                Thread.Sleep(milliseconds >= 1.0 ? (int)Math.Round(milliseconds) : 1);
                return;
            }

            double coarse = milliseconds - SpinMilliseconds;
            if (coarse > 0.05)
            {
                // A negative due time is relative, in units of 100 ns.
                long due = -(long)Math.Round(coarse * 10000.0);
                if (!SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    Thread.Sleep((int)Math.Round(coarse));
                    return;
                }
                WaitForSingleObject(handle, Infinite);
                Spin(SpinMilliseconds);
                return;
            }

            Spin(milliseconds);
        }

        private static void Spin(double milliseconds)
        {
            if (milliseconds <= 0.0) return;
            long until = Stopwatch.GetTimestamp() +
                (long)(milliseconds * Stopwatch.Frequency / 1000.0);
            // Thread.SpinWait issues the processor's own pause instruction and never yields.
            // SpinWait.SpinOnce looks like the obvious choice here and is not: it yields to the
            // scheduler after a few turns and ends up calling Thread.Sleep(1), which on a
            // system that has not raised its timer resolution sleeps for a whole 15.6 ms tick.
            // That turned a "wait 0.8 ms" into a 15 ms stall on half the packets, and a sender
            // that jitters by 15 ms is one a receiver has to keep correcting for.
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);
        }
    }
}
