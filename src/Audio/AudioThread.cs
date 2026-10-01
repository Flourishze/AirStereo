using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirStereo.Audio
{
    /// <summary>
    /// Raises a worker thread into the multimedia class scheduler.
    ///
    /// A thread that has to wake every eight milliseconds is at the mercy of the ordinary
    /// Windows scheduler: a normal priority thread can lose its slice to a background task and
    /// come back several milliseconds late, which at a 250 ms buffer is a tenth of the slack
    /// the receiver has. The "Pro Audio" class asks the scheduler to treat the thread like a
    /// professional audio stream, which both raises it and gives it a fixed servicing
    /// quantum. Nothing here is required for correctness - every caller still checks its own
    /// timing - so a failure is simply ignored.
    /// </summary>
    public static class AudioThread
    {
        private const int CriticalPriority = 2;   // AVRT_PRIORITY_CRITICAL
        [ThreadStatic] private static bool raised;

        [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out int taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

        public static void Raise()
        {
            if (raised) return;
            raised = true;
            try
            {
                int index;
                IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio", out index);
                if (handle != IntPtr.Zero)
                {
                    AvSetMmThreadPriority(handle, CriticalPriority);
                    return;
                }
            }
            catch (Exception)
            {
                // avrt.dll is missing on stripped down systems; fall through to the plain
                // priority bump below.
            }
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch (Exception)
            {
                // Priority changes can be refused; the thread still runs.
            }
        }
    }
}
