using System;
using System.Threading;

namespace AirStereo.Audio
{
    public enum ChannelTest { None, Left, Right, Stereo }

    /// <summary>UI commands shared with one audio thread; never replaces the active sessions.</summary>
    public sealed class LivePlaybackControl
    {
        private readonly bool supportsBalance;
        private int balance;
        private int pendingTest;
        private ChannelTest activeTest;
        private long testPosition;
        private bool testBlock;

        public LivePlaybackControl(bool supportsBalance) { this.supportsBalance = supportsBalance; }

        public int Balance
        {
            get { return supportsBalance ? Volatile.Read(ref balance) : 0; }
            set { Volatile.Write(ref balance, Math.Max(-100, Math.Min(100, value))); }
        }

        public void StartTest(ChannelTest channel)
        {
            if (channel < ChannelTest.Left || channel > ChannelTest.Stereo)
                throw new ArgumentOutOfRangeException(nameof(channel));
            Interlocked.Exchange(ref pendingTest, (int)channel);
        }

        /// <summary>Called before EQ. Keep consuming capture while a test replaces its samples.</summary>
        public void PrepareBlock(short[] samples, int sampleRate)
        {
            int pending = Interlocked.Exchange(ref pendingTest, 0);
            if (pending != 0) { activeTest = (ChannelTest)pending; testPosition = 0; }
            testBlock = activeTest != ChannelTest.None;
            if (!testBlock) return;
            long total = sampleRate * 3L;
            double fade = sampleRate * 0.04;
            for (int i = 0; i < samples.Length; i += 2)
            {
                double envelope = testPosition >= total ? 0 :
                    Math.Min(1.0, Math.Min(testPosition / fade, (total - testPosition) / fade));
                double time = testPosition / (double)sampleRate;
                samples[i] = activeTest == ChannelTest.Right ? (short)0 :
                    (short)(Math.Sin(2 * Math.PI * 440 * time) * 0.25 * short.MaxValue * envelope);
                samples[i + 1] = activeTest == ChannelTest.Left ? (short)0 :
                    (short)(Math.Sin(2 * Math.PI * 660 * time) * 0.25 * short.MaxValue * envelope);
                testPosition++;
            }
            if (testPosition >= total) activeTest = ChannelTest.None;
        }

        /// <summary>Called after EQ, before StereoRouting splits the shared sample block.</summary>
        public void ApplyBalance(short[] samples)
        {
            if (!testBlock) StereoRouting.ApplyBalanceInPlace(samples, Balance);
        }
    }

    public sealed class LivePlaybackSource : AudioSource, IDisposable
    {
        private readonly AudioSource source;
        private readonly LivePlaybackControl control;
        public LivePlaybackSource(AudioSource source, LivePlaybackControl control)
        {
            this.source = source;
            this.control = control;
        }
        public override int SampleRate { get { return source.SampleRate; } }
        public override int Read(short[] buffer, int frames)
        {
            int count = source.Read(buffer, frames);
            control.PrepareBlock(buffer, SampleRate);
            return count;
        }
        public override void Prepare() { source.Prepare(); }
        public override void Stop() { source.Stop(); }
        public override string Stats() { return source.Stats(); }
        public void Dispose() { (source as IDisposable)?.Dispose(); }
    }
}
