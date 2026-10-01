using System;

namespace AirStereo.Audio
{
    /// <summary>
    /// Two channel streaming sample rate converter with a windowed sinc kernel.
    ///
    /// The capture path has to turn the endpoint's mix rate into the stream rate, and on this
    /// machine that is 48000 into 44100. Linear interpolation, which this replaces, is a two
    /// tap filter: everything above the output Nyquist folds back into the band as aliasing.
    /// Under a large buffer that is quiet enough to ignore, but it sits on top of the music as
    /// a harsh, granular layer - what a listener calls "noise mixed in" - and it is worst for
    /// cymbals and sibilants, which carry most of their energy up there.
    ///
    /// The kernel is tabulated once per rate pair (1024 phases, 32 taps), and the conversion
    /// ratio can be trimmed by a few hundred parts per million, which the capture path uses to
    /// hold its queue depth steady against the endpoint's own crystal.
    /// </summary>
    public sealed class SincResampler
    {
        public const int HalfTaps = 16;
        private const int Phases = 1024;
        private const int KernelLength = HalfTaps * 2;
        /// <summary>The largest rate trim the caller may ask for, 1500 ppm or 0.15%.</summary>
        public const double MaxTrim = 0.0015;

        private readonly double baseStep;
        private readonly float[] kernel = new float[Phases * KernelLength];
        private readonly float[] gain = new float[Phases];
        private float[] pending = new float[8192];
        private int pendingFrames;
        /// <summary>Fractional position of the next output frame inside the pending buffer.</summary>
        private double position;
        private double trim;
        private double step;
        private float[] produced = new float[8192];
        private int producedFrames;
        /// <summary>True when the two rates match: nothing to convert and nothing to trim.</summary>
        private readonly bool passthrough;

        public SincResampler(int captureRate, int outputRate)
        {
            if (captureRate <= 0) throw new ArgumentOutOfRangeException("captureRate");
            if (outputRate <= 0) throw new ArgumentOutOfRangeException("outputRate");
            baseStep = captureRate / (double)outputRate;
            passthrough = captureRate == outputRate;

            // The kernel must not pass anything the output band cannot hold: when the endpoint
            // runs faster than the stream, the top of the captured band has to be filtered out
            // instead of folded back down.
            double cutoff = 0.92 * Math.Min(1.0, 1.0 / baseStep);
            for (int phase = 0; phase < Phases; phase++)
            {
                double fraction = phase / (double)Phases;
                double sum = 0.0;
                for (int tap = 0; tap < KernelLength; tap++)
                {
                    double offset = (tap - (HalfTaps - 1)) - fraction;
                    double value = cutoff * Sinc(cutoff * offset) * Window(offset / HalfTaps);
                    kernel[phase * KernelLength + tap] = (float)value;
                    sum += value;
                }
                // Normalising per phase keeps the level exactly flat as the fractional position
                // walks across the table; an unnormalised kernel would breathe by a few
                // hundredths of a decibel, which is a modulation nobody wants on music.
                gain[phase] = Math.Abs(sum) > 1e-6 ? (float)(1.0 / sum) : 1f;
            }
            step = baseStep;
        }

        /// <summary>Input frames that have arrived but have not been converted yet.</summary>
        public int PendingInputFrames { get { return pendingFrames - (int)position; } }

        /// <summary>Interleaved stereo output of the last <see cref="Push"/>.</summary>
        public float[] Produced { get { return produced; } }

        public int ProducedFrames { get { return producedFrames; } }

        /// <summary>The current rate trim, in parts per million of the nominal ratio.</summary>
        public double TrimPpm { get { return trim * 1e6; } }

        /// <summary>
        /// Trims the conversion ratio: negative values produce more output from the same input
        /// and so fill the caller's queue, positive values drain it. The range is deliberately
        /// tiny; a few hundred ppm is inaudible on music and is all a crystal mismatch needs.
        /// </summary>
        public void SetTrim(double relative)
        {
            if (passthrough) return;
            if (relative > MaxTrim) relative = MaxTrim;
            else if (relative < -MaxTrim) relative = -MaxTrim;
            trim = relative;
            step = baseStep * (1.0 + relative);
        }

        public void Reset()
        {
            pendingFrames = 0;
            position = 0.0;
            producedFrames = 0;
        }

        /// <summary>Appends captured frames and converts everything that is now decidable.</summary>
        public void Push(float[] interleaved, int frames)
        {
            if (frames <= 0)
            {
                producedFrames = 0;
                return;
            }

            if (passthrough)
            {
                if (produced.Length < frames * 2) produced = new float[frames * 2];
                Array.Copy(interleaved, 0, produced, 0, frames * 2);
                producedFrames = frames;
                return;
            }

            // The taps before the current position are the only history that matters, and the
            // device hands us ten milliseconds every time, so the buffer is compacted on every
            // call instead of growing.
            DropConsumed(0);
            EnsurePending(pendingFrames + frames);
            Array.Copy(interleaved, 0, pending, pendingFrames * 2, frames * 2);
            pendingFrames += frames;
            Convert();
        }

        private void EnsurePending(int frames)
        {
            if (pending.Length >= frames * 2) return;
            int size = pending.Length;
            while (size < frames * 2) size *= 2;
            float[] grown = new float[size];
            Array.Copy(pending, 0, grown, 0, pendingFrames * 2);
            pending = grown;
        }

        private void DropConsumed(int keepMinimum)
        {
            int consumed = (int)position - HalfTaps + 1;
            if (consumed <= keepMinimum) return;
            int keep = pendingFrames - consumed;
            if (keep > 0) Array.Copy(pending, consumed * 2, pending, 0, keep * 2);
            pendingFrames = keep > 0 ? keep : 0;
            position -= consumed;
        }

        private void Convert()
        {
            producedFrames = 0;
            int limit = pendingFrames - 1 - HalfTaps;
            if (limit < 0) return;

            int needed = (int)(pendingFrames / Math.Max(step, 0.25)) + 4;
            if (produced.Length < needed * 2)
            {
                int size = produced.Length;
                while (size < needed * 2) size *= 2;
                produced = new float[size];
            }

            int count = 0;
            int available = pendingFrames * 2;
            while ((int)position <= limit)
            {
                int index = (int)position;
                int phase = (int)((position - index) * Phases);
                if (phase >= Phases) phase = Phases - 1;
                int row = phase * KernelLength;
                float normalise = gain[phase];

                for (int channel = 0; channel < 2; channel++)
                {
                    double sum = 0.0;
                    int first = (index - (HalfTaps - 1)) * 2 + channel;
                    for (int tap = 0; tap < KernelLength; tap++)
                    {
                        int at = first + tap * 2;
                        if (at < 0) at = channel;
                        else if (at >= available) at = available - 2 + channel;
                        sum += kernel[row + tap] * pending[at];
                    }
                    produced[count * 2 + channel] = (float)(sum * normalise);
                }

                count++;
                position += step;
            }
            producedFrames = count;
        }

        private static double Sinc(double value)
        {
            if (value == 0.0) return 1.0;
            double angle = Math.PI * value;
            return Math.Sin(angle) / angle;
        }

        /// <summary>Four term Blackman-Harris, evaluated on the normalised tap position.</summary>
        private static double Window(double position)
        {
            if (position <= -1.0 || position >= 1.0) return 0.0;
            double angle = Math.PI * position;
            return 0.35875 + 0.48829 * Math.Cos(angle) + 0.14128 * Math.Cos(2 * angle) +
                0.01168 * Math.Cos(3 * angle);
        }
    }
}
