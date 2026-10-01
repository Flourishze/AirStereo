using System;

namespace AirStereo.Audio
{
    /// <summary>Splits one post-EQ interleaved stereo block into two stereo-compatible mono streams.</summary>
    public static class StereoRouting
    {
        public static void SwapInPlace(short[] samples)
        {
            if (samples == null || samples.Length % 2 != 0)
                throw new ArgumentException("samples must be interleaved stereo");
            for (int i = 0; i < samples.Length; i += 2)
            {
                short left = samples[i];
                samples[i] = samples[i + 1];
                samples[i + 1] = left;
            }
        }

        /// <summary>Attenuate the opposite output side after swapping and EQ.</summary>
        public static void ApplyBalanceInPlace(short[] samples, int balance)
        {
            if (balance < -100 || balance > 100) throw new ArgumentOutOfRangeException("balance");
            if (samples == null || samples.Length % 2 != 0)
                throw new ArgumentException("samples must be interleaved stereo");
            if (balance == 0) return;
            double leftGain = balance > 0 ? (100 - balance) / 100.0 : 1.0;
            double rightGain = balance < 0 ? (100 + balance) / 100.0 : 1.0;
            for (int i = 0; i < samples.Length; i += 2)
            {
                samples[i] = (short)Math.Round(samples[i] * leftGain);
                samples[i + 1] = (short)Math.Round(samples[i + 1] * rightGain);
            }
        }

        public static void Split(short[] samples, byte[] leftPcm, byte[] rightPcm, int balance)
        {
            if (balance < -100 || balance > 100) throw new ArgumentOutOfRangeException("balance");
            if (samples == null || samples.Length % 2 != 0 || leftPcm == null || rightPcm == null ||
                leftPcm.Length < samples.Length * 2 || rightPcm.Length < samples.Length * 2)
                throw new ArgumentException("stereo PCM buffers must match the interleaved block");
            double leftGain = balance > 0 ? (100 - balance) / 100.0 : 1.0;
            double rightGain = balance < 0 ? (100 + balance) / 100.0 : 1.0;
            for (int i = 0; i < samples.Length; i += 2)
            {
                short left = (short)Math.Round(samples[i] * leftGain);
                short right = (short)Math.Round(samples[i + 1] * rightGain);
                int offset = i * 2;
                // Duplicate a mono side into both slots for receivers that expect stereo PCM.
                leftPcm[offset] = leftPcm[offset + 2] = (byte)(left >> 8);
                leftPcm[offset + 1] = leftPcm[offset + 3] = (byte)left;
                rightPcm[offset] = rightPcm[offset + 2] = (byte)(right >> 8);
                rightPcm[offset + 1] = rightPcm[offset + 3] = (byte)right;
            }
        }
    }
}
