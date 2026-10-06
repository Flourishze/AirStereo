using System;
using System.Threading;

namespace AirStereo.Audio
{
    /// <summary>
    /// Immutable DSP settings shared by the window and the realtime audio thread. A new
    /// instance is published atomically, so changing a slider never leaves a half updated
    /// filter running on the audio thread.
    /// </summary>
    public sealed class AudioProfile
    {
        public const int BandCount = 5;

        private static readonly double[] Frequencies = { 60.0, 230.0, 910.0, 3600.0, 12000.0 };
        private static readonly double[] Qs = { 0.72, 0.90, 1.00, 0.90, 0.72 };
        private static readonly AudioProfile FlatProfile =
            new AudioProfile(false, new double[BandCount], 0.0, 0.0);

        private readonly double[] bandGainsDb;

        public AudioProfile(bool enabled, double[] gainsDb, double leftGainDb, double rightGainDb)
        {
            double[] copied = new double[BandCount];
            if (gainsDb != null)
            {
                for (int i = 0; i < BandCount && i < gainsDb.Length; i++)
                {
                    copied[i] = Clamp(gainsDb[i], -12.0, 12.0);
                }
            }
            Enabled = enabled;
            bandGainsDb = copied;
            LeftGainDb = Clamp(leftGainDb, -12.0, 12.0);
            RightGainDb = Clamp(rightGainDb, -12.0, 12.0);
        }

        public static AudioProfile Flat
        {
            get { return FlatProfile; }
        }

        public bool Enabled { get; private set; }
        public double LeftGainDb { get; private set; }
        public double RightGainDb { get; private set; }

        public double BandGainDb(int index)
        {
            return index >= 0 && index < BandCount ? bandGainsDb[index] : 0.0;
        }

        public static double Frequency(int index)
        {
            return index >= 0 && index < BandCount ? Frequencies[index] : 0.0;
        }

        public static double Q(int index)
        {
            return index >= 0 && index < BandCount ? Qs[index] : 1.0;
        }

        /// <summary>
        /// Keeps a positive EQ curve from clipping the 16 bit transport too early. The user
        /// still gets the requested tonal balance, but the whole output is lowered by the
        /// largest boost instead of relying on a hard limiter.
        /// </summary>
        public double AutoPreampDb
        {
            get
            {
                double largestBoost = 0.0;
                for (int i = 0; i < BandCount; i++)
                {
                    if (bandGainsDb[i] > largestBoost) largestBoost = bandGainsDb[i];
                }
                return -largestBoost;
            }
        }

        public double EffectiveLeftGainDb { get { return LeftGainDb + AutoPreampDb; } }
        public double EffectiveRightGainDb { get { return RightGainDb + AutoPreampDb; } }

        public bool IsFlat
        {
            get
            {
                if (!Enabled) return true;
                if (Math.Abs(LeftGainDb) > 0.001 || Math.Abs(RightGainDb) > 0.001) return false;
                for (int i = 0; i < BandCount; i++)
                {
                    if (Math.Abs(bandGainsDb[i]) > 0.001) return false;
                }
                return true;
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }

    /// <summary>Thread-safe holder for the live profile used by a playing stream.</summary>
    public sealed class AudioProfileController
    {
        private AudioProfile profile = AudioProfile.Flat;

        public AudioProfile Current
        {
            get { return Volatile.Read(ref profile); }
        }

        public void Update(AudioProfile value)
        {
            Volatile.Write(ref profile, value ?? AudioProfile.Flat);
        }
    }

    /// <summary>
    /// Five peaking filters followed by independent left/right gain. It runs in the same
    /// allocation-free path as the packetizer and only rebuilds coefficients when the
    /// immutable profile object changes.
    /// </summary>
    public sealed class CalibratedAudioSource : AudioSource, IDisposable
    {
        private readonly AudioSource inner;
        private readonly AudioProfileController controller;
        private Biquad[] leftFilters;
        private Biquad[] rightFilters;
        private BiquadState[] leftState;
        private BiquadState[] rightState;
        private AudioProfile current;
        private double leftGain;
        private double rightGain;

        public CalibratedAudioSource(AudioSource inner, AudioProfileController controller)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            this.controller = controller ?? new AudioProfileController();
            leftFilters = new Biquad[AudioProfile.BandCount];
            rightFilters = new Biquad[AudioProfile.BandCount];
            leftState = new BiquadState[AudioProfile.BandCount];
            rightState = new BiquadState[AudioProfile.BandCount];
            ApplyProfile(this.controller.Current);
        }

        public override int SampleRate { get { return inner.SampleRate; } }

        public override bool IsRealtime { get { return inner.IsRealtime; } }

        public override AudioReadActivity LastReadActivity
        {
            get { return inner.LastReadActivity; }
        }

        public override int Read(short[] buffer, int frames)
        {
            int read = inner.Read(buffer, frames);
            AudioProfile profile = controller.Current;
            if (!ReferenceEquals(profile, current)) ApplyProfile(profile);
            if (profile.IsFlat) return read;

            for (int frame = 0; frame < read; frame++)
            {
                int index = frame * 2;
                double left = buffer[index] / 32768.0;
                double right = buffer[index + 1] / 32768.0;
                for (int band = 0; band < AudioProfile.BandCount; band++)
                {
                    left = leftState[band].Process(left, leftFilters[band]);
                    right = rightState[band].Process(right, rightFilters[band]);
                }
                left *= leftGain;
                right *= rightGain;
                buffer[index] = ToSample(left);
                buffer[index + 1] = ToSample(right);
            }
            return read;
        }

        public override void Prepare()
        {
            inner.Prepare();
        }

        public override void PrepareForResume()
        {
            inner.PrepareForResume();
            for (int band = 0; band < leftState.Length; band++)
            {
                leftState[band].Reset();
                rightState[band].Reset();
            }
        }

        public override void Stop()
        {
            inner.Stop();
        }

        public override string Stats()
        {
            return inner.Stats();
        }

        public void Dispose()
        {
            IDisposable disposable = inner as IDisposable;
            if (disposable != null) disposable.Dispose();
        }

        private void ApplyProfile(AudioProfile profile)
        {
            current = profile ?? AudioProfile.Flat;
            int rate = Math.Max(8000, inner.SampleRate);
            for (int band = 0; band < AudioProfile.BandCount; band++)
            {
                double gain = current.BandGainDb(band);
                Biquad filter = Biquad.Peaking(rate, AudioProfile.Frequency(band), AudioProfile.Q(band), gain);
                leftFilters[band] = filter;
                rightFilters[band] = filter;
            }
            leftGain = DecibelsToLinear(current.EffectiveLeftGainDb);
            rightGain = DecibelsToLinear(current.EffectiveRightGainDb);
        }

        private static double DecibelsToLinear(double decibels)
        {
            return Math.Pow(10.0, decibels / 20.0);
        }

        private static short ToSample(double value)
        {
            if (value > 1.0) value = 1.0;
            else if (value < -1.0) value = -1.0;
            return (short)Math.Round(value * 32767.0);
        }

        private struct Biquad
        {
            public double B0;
            public double B1;
            public double B2;
            public double A1;
            public double A2;

            public static Biquad Peaking(int sampleRate, double frequency, double q, double gainDb)
            {
                double a = Math.Pow(10.0, gainDb / 40.0);
                double omega = 2.0 * Math.PI * frequency / sampleRate;
                double alpha = Math.Sin(omega) / (2.0 * q);
                double a0 = 1.0 + alpha / a;
                Biquad result;
                result.B0 = (1.0 + alpha * a) / a0;
                result.B1 = (-2.0 * Math.Cos(omega)) / a0;
                result.B2 = (1.0 - alpha * a) / a0;
                result.A1 = (-2.0 * Math.Cos(omega)) / a0;
                result.A2 = (1.0 - alpha / a) / a0;
                return result;
            }
        }

        private struct BiquadState
        {
            private double x1;
            private double x2;
            private double y1;
            private double y2;

            public void Reset()
            {
                x1 = 0.0;
                x2 = 0.0;
                y1 = 0.0;
                y2 = 0.0;
            }

            public double Process(double input, Biquad filter)
            {
                double output = filter.B0 * input + filter.B1 * x1 + filter.B2 * x2 -
                    filter.A1 * y1 - filter.A2 * y2;
                x2 = x1;
                x1 = input;
                y2 = y1;
                y1 = output;
                if (Math.Abs(output) < 1.0e-20) output = 0.0;
                return output;
            }
        }
    }
}
