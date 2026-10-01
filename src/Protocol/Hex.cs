using System;
using System.Numerics;
using System.Text;

namespace AirStereo.Protocol
{
    public static class Hex
    {
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
            StringBuilder clean = new StringBuilder(hex.Length);
            foreach (char character in hex)
            {
                if (!char.IsWhiteSpace(character)) clean.Append(character);
            }
            return Convert.FromHexString(clean.ToString());
        }

        public static string ToHex(byte[] data)
        {
            return data == null ? "" : Convert.ToHexString(data).ToLowerInvariant();
        }

        /// <summary>Big-endian bytes to a positive BigInteger.</summary>
        public static BigInteger ToNumber(byte[] bigEndian)
        {
            return new BigInteger(bigEndian, isUnsigned: true, isBigEndian: true);
        }

        /// <summary>BigInteger to big-endian bytes, optionally left padded to a fixed length.</summary>
        public static byte[] FromNumber(BigInteger value, int padTo = 0)
        {
            byte[] big = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (padTo <= big.Length) return big;
            byte[] padded = new byte[padTo];
            Buffer.BlockCopy(big, 0, padded, padTo - big.Length, big.Length);
            return padded;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            return HapCrypto.Concat(parts);
        }
    }
}
