using System;
using AirStereo.Audio;

namespace AirStereo.Session
{
    public enum SilenceFrameMode { RepeatLast, Zero }

    /// <summary>Capture-confirmed silence only; never measures connection age.</summary>
    internal sealed class ContinuousSilencePolicy
    {
        private readonly int disconnectMilliseconds;
        private readonly SilenceFrameMode mode;
        private readonly short[] lastSilent;
        private bool hasSilentBlock;
        internal double SilentMilliseconds { get; private set; }

        internal ContinuousSilencePolicy(int disconnectMilliseconds, SilenceFrameMode mode, int samples)
        {
            if (disconnectMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(disconnectMilliseconds));
            if (mode != SilenceFrameMode.RepeatLast && mode != SilenceFrameMode.Zero)
                throw new ArgumentOutOfRangeException(nameof(mode));
            this.disconnectMilliseconds = disconnectMilliseconds;
            this.mode = mode;
            lastSilent = new short[samples];
        }

        internal void Reset()
        {
            SilentMilliseconds = 0;
            hasSilentBlock = false;
        }

        internal bool Process(short[] block, bool realtime, AudioReadActivity activity, int frames, int rate)
        {
            if (!realtime || activity != AudioReadActivity.ConfirmedSilent || Streamer.HasAudioActivity(block))
            {
                // Missing/unknown capture is not evidence of continuous source silence.
                Reset();
                return false;
            }
            SilentMilliseconds += frames * 1000.0 / rate;
            if (mode == SilenceFrameMode.Zero) Array.Clear(block, 0, block.Length);
            else if (hasSilentBlock) Array.Copy(lastSilent, block, block.Length);
            else
            {
                // Seed only from confirmed silence, NEVER loop the last audible music block.
                Array.Copy(block, lastSilent, block.Length);
                hasSilentBlock = true;
            }
            return SilentMilliseconds >= disconnectMilliseconds;
        }
    }
}
