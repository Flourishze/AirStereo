using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using AirStereo.Protocol;

namespace AirStereo.Audio
{
    /// <summary>Capture-side confidence for the audio block most recently returned.</summary>
    public enum AudioReadActivity
    {
        Unknown,
        Active,
        ConfirmedSilent,
        Starved
    }

    /// <summary>Interleaved 16 bit stereo audio, delivered in fixed frame blocks.</summary>
    public abstract class AudioSource
    {
        public abstract int SampleRate { get; }

        /// <summary>True when the source follows a live desktop audio stream.</summary>
        public virtual bool IsRealtime { get { return false; } }

        /// <summary>Activity reported for the most recent read. Non-capture sources are active.</summary>
        public virtual AudioReadActivity LastReadActivity { get { return AudioReadActivity.Active; } }

        /// <summary>Fills <paramref name="buffer"/> with interleaved stereo samples.</summary>
        public abstract int Read(short[] buffer, int frames);

        /// <summary>
        /// Gives the source a chance to fill its own pipeline before the first packet goes out.
        /// A file or a tone has nothing to do here; a live capture uses it to settle its queue.
        /// </summary>
        public virtual void Prepare()
        {
        }

        /// <summary>
        /// Rebuilds a live capture pipeline before a paused stream is resumed. File and tone
        /// sources do not need this; WASAPI loopback uses it to stop recording, clear stale PCM,
        /// recreate the capture client and then restart.
        /// </summary>
        public virtual void PrepareForResume()
        {
        }

        /// <summary>Interrupts a live source when the sender is asked to stop.</summary>
        public virtual void Stop()
        {
        }

        /// <summary>Source health for the log, or an empty string when there is nothing to say.</summary>
        public virtual string Stats()
        {
            return "";
        }

        public static byte[] ToBigEndianPcm(short[] samples, int count)
        {
            byte[] bytes = new byte[count * 2];
            ToBigEndianPcm(samples, count, bytes);
            return bytes;
        }

        /// <summary>
        /// Same, into a buffer the caller owns. The send loop calls this once per packet, and
        /// an allocation there is garbage collection the audio cadence eventually pays for.
        /// </summary>
        public static void ToBigEndianPcm(short[] samples, int count, byte[] bytes)
        {
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), samples[i]);
            }
        }
    }

    /// <summary>
    /// Stereo verification signal: a different tone in each channel, so a listener can hear
    /// immediately whether the pair is playing left and right properly or duplicating one
    /// channel onto both speakers.
    /// </summary>
    public sealed class ToneSource : AudioSource
    {
        private readonly double leftHz;
        private readonly double rightHz;
        private readonly double amplitude;
        private double position;

        public ToneSource(int sampleRate, double leftHz = 440.0, double rightHz = 660.0, double amplitude = 0.25)
        {
            SampleRate = sampleRate;
            this.leftHz = leftHz;
            this.rightHz = rightHz;
            this.amplitude = amplitude;
        }

        public override int SampleRate { get; }

        public override int Read(short[] buffer, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                double time = position / SampleRate;
                buffer[frame * 2] = (short)(Math.Sin(2 * Math.PI * leftHz * time) * amplitude * short.MaxValue);
                buffer[frame * 2 + 1] = (short)(Math.Sin(2 * Math.PI * rightHz * time) * amplitude * short.MaxValue);
                position += 1;

                // Fade the tone out every eight seconds so long sessions stay comfortable.
                double cycle = position / SampleRate % 8.0;
                double envelope = cycle < 0.05 ? cycle / 0.05 : (cycle > 7.95 ? (8.0 - cycle) / 0.05 : 1.0);
                if (envelope < 1.0)
                {
                    buffer[frame * 2] = (short)(buffer[frame * 2] * envelope);
                    buffer[frame * 2 + 1] = (short)(buffer[frame * 2 + 1] * envelope);
                }
            }
            return frames;
        }
    }

    /// <summary>
    /// A channel identification pattern for checking a stereo pair by ear. The first phase
    /// carries sound in the left channel only, the second in the right channel only, and the
    /// third in both. If the first phase is audible from both speakers, the pair is not being
    /// rendered as left and right.
    /// </summary>
    public sealed class ChannelCheckSource : AudioSource
    {
        private readonly double amplitude;
        private readonly double phaseSeconds;
        private readonly double leftHz;
        private readonly double rightHz;
        private long position;

        public ChannelCheckSource(int sampleRate, double phaseSeconds = 4.0,
            double leftHz = 440.0, double rightHz = 660.0, double amplitude = 0.25)
        {
            SampleRate = sampleRate;
            this.phaseSeconds = phaseSeconds;
            this.leftHz = leftHz;
            this.rightHz = rightHz;
            this.amplitude = amplitude;
        }

        public override int SampleRate { get; }

        /// <summary>0 left, 1 right, 2 both, 3 both again (the pattern repeats).</summary>
        public int Phase { get { return (int)((position / (double)SampleRate) / phaseSeconds) % 3; } }

        public override int Read(short[] buffer, int frames)
        {
            long phaseFrames = (long)(phaseSeconds * SampleRate);
            for (int frame = 0; frame < frames; frame++)
            {
                int phase = Phase;
                double time = position / (double)SampleRate;
                double left = phase == 1 ? 0.0 : Math.Sin(2 * Math.PI * leftHz * time) * amplitude;
                double right = phase == 0 ? 0.0 : Math.Sin(2 * Math.PI * rightHz * time) * amplitude;

                // Fade over 40 ms at every phase boundary so the transitions do not click.
                long intoPhase = position % phaseFrames;
                double fade = 0.04 * SampleRate;
                double envelope = 1.0;
                if (intoPhase < fade) envelope = intoPhase / fade;
                else if (phaseFrames - intoPhase < fade) envelope = (phaseFrames - intoPhase) / fade;
                // Phase two starts silently on the left channel, so it only needs the same fade.
                if (envelope < 0.0) envelope = 0.0;

                buffer[frame * 2] = (short)(left * envelope * short.MaxValue);
                buffer[frame * 2 + 1] = (short)(right * envelope * short.MaxValue);
                position++;
            }
            return frames;
        }
    }

    /// <summary>
    /// The same slow sweep sent to both speakers. A stereo pair that is perfectly aligned puts
    /// the sweep in the middle of the room as one clean tone; a pair whose two halves are
    /// playing a fraction of a millisecond apart combs the sweep into a warble, with deep
    /// notches walking up and down as the frequency rises. That warble is what a listener
    /// hears as a hollow, noisy version of music, and it is the one thing no sender side
    /// counter can measure: it happens between the two speakers, after the audio has left this
    /// process. Five seconds up, then it starts again.
    /// </summary>
    public sealed class MonoSweepSource : AudioSource
    {
        private const double SweepSeconds = 5.0;
        private const double LowHz = 100.0;
        private const double HighHz = 4000.0;
        private readonly double amplitude;
        private double position;
        private double phase;

        public MonoSweepSource(int sampleRate, double amplitude = 0.2)
        {
            SampleRate = sampleRate;
            this.amplitude = amplitude;
        }

        public override int SampleRate { get; }

        public override int Read(short[] buffer, int frames)
        {
            double cycle = SweepSeconds * SampleRate;
            for (int frame = 0; frame < frames; frame++)
            {
                double into = position % cycle;
                double progress = into / cycle;
                double frequency = LowHz + (HighHz - LowHz) * progress;
                double envelope = 1.0;
                double fade = 0.05 * SampleRate;
                if (into < fade) envelope = into / fade;
                else if (cycle - into < fade) envelope = (cycle - into) / fade;

                // The phase is accumulated, so the tone glides instead of stepping once per
                // block, and it is wrapped to keep the argument small over a long session.
                short sample = (short)(Math.Sin(phase) *
                    amplitude * envelope * short.MaxValue);
                buffer[frame * 2] = sample;
                buffer[frame * 2 + 1] = sample;
                phase += 2.0 * Math.PI * frequency / SampleRate;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                position++;
            }
            return frames;
        }
    }

    /// <summary>Sixteen bit PCM WAV file, mono or stereo. Loops when it reaches the end.</summary>
    public sealed class WavSource : AudioSource
    {
        private readonly short[] samples;      // interleaved stereo
        private readonly int sourceFrames;
        private readonly SincResampler resampler;
        /// <summary>True when the file already runs at the stream rate: nothing to convert.</summary>
        private readonly bool direct;
        private readonly Queue<short> converted = new Queue<short>();
        private float[] input = new float[0];
        private long position;

        private WavSource(short[] interleaved, int sourceRate, int targetRate)
        {
            SampleRate = targetRate;
            samples = interleaved;
            sourceFrames = interleaved.Length / 2;
            direct = sourceRate == targetRate;
            resampler = direct ? null : new SincResampler(sourceRate, targetRate);
        }

        public static WavSource Open(string path, int targetRate)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
            {
                throw new ProtocolException("not a RIFF file: " + path);
            }

            int channels = 0;
            int bits = 0;
            int rate = 0;
            int format = 0;
            int dataStart = -1;
            int dataLength = 0;
            int cursor = 12;
            while (cursor + 8 <= data.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(data, cursor, 4);
                int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor + 4));
                int body = cursor + 8;
                if (id == "fmt " && size >= 16 && body + 16 <= data.Length)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                    rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
                }
                else if (id == "data")
                {
                    dataStart = body;
                    dataLength = Math.Min(size, data.Length - body);
                }
                cursor = body + size + (size & 1);
            }

            if (format != 1 || bits != 16 || dataStart < 0 || channels < 1 || channels > 2 || rate <= 0)
            {
                throw new ProtocolException("WAV must be 16 bit PCM, mono or stereo");
            }

            int frames = dataLength / (2 * channels);
            short[] interleaved = new short[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                short first = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(dataStart + frame * 2 * channels));
                short second = channels == 2
                    ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(dataStart + frame * 2 * channels + 2))
                    : first;
                interleaved[frame * 2] = first;
                interleaved[frame * 2 + 1] = second;
            }
            return new WavSource(interleaved, rate, targetRate);
        }

        public override int SampleRate { get; }

        public override int Read(short[] buffer, int frames)
        {
            if (samples.Length == 0)
            {
                Array.Clear(buffer, 0, frames * 2);
                return frames;
            }

            if (direct)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    long at = position % sourceFrames;
                    buffer[frame * 2] = samples[at * 2];
                    buffer[frame * 2 + 1] = samples[at * 2 + 1];
                    position++;
                }
                return frames;
            }

            int needed = frames * 2;
            int written = 0;
            while (written < needed)
            {
                while (written < needed && converted.Count > 0) buffer[written++] = converted.Dequeue();
                if (written >= needed) break;

                const int chunk = 2048;
                if (input.Length < chunk * 2) input = new float[chunk * 2];
                for (int frame = 0; frame < chunk; frame++)
                {
                    if (position >= sourceFrames) position = 0;   // the file loops
                    input[frame * 2] = samples[position * 2] / 32768f;
                    input[frame * 2 + 1] = samples[position * 2 + 1] / 32768f;
                    position++;
                }

                resampler.Push(input, chunk);
                float[] produced = resampler.Produced;
                int producedFrames = resampler.ProducedFrames;
                for (int i = 0; i < producedFrames; i++)
                {
                    converted.Enqueue(ToSample(produced[i * 2]));
                    converted.Enqueue(ToSample(produced[i * 2 + 1]));
                }
            }
            return frames;
        }

        private static short ToSample(float value)
        {
            if (value > 1f) value = 1f;
            else if (value < -1f) value = -1f;
            // Rounded, not truncated, for the same reason the loopback path rounds: a
            // truncation bias is a quiet but permanent layer of distortion on the audio.
            return (short)Math.Round(value * 32767f);
        }
    }

    /// <summary>Multiplies another source by a fixed gain, clamped to the 16 bit range.</summary>
    public sealed class GainSource : AudioSource, IDisposable
    {
        private readonly AudioSource inner;
        private readonly double gain;

        public GainSource(AudioSource inner, double gain)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            this.gain = gain;
        }

        public override int SampleRate { get { return inner.SampleRate; } }
        public override bool IsRealtime { get { return inner.IsRealtime; } }
        public override AudioReadActivity LastReadActivity { get { return inner.LastReadActivity; } }

        public override void PrepareForResume()
        {
            inner.PrepareForResume();
        }

        public override int Read(short[] buffer, int frames)
        {
            int read = inner.Read(buffer, frames);
            for (int i = 0; i < read * 2; i++)
            {
                int value = (int)(buffer[i] * gain);
                if (value > short.MaxValue) value = short.MaxValue;
                else if (value < short.MinValue) value = short.MinValue;
                buffer[i] = (short)value;
            }
            return read;
        }

        public override void Stop()
        {
            inner.Stop();
        }

        public void Dispose()
        {
            (inner as IDisposable)?.Dispose();
        }
    }
}





