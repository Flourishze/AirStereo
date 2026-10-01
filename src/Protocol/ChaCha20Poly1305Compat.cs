using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace AirStereo.Protocol
{
    /// <summary>
    /// Managed RFC 8439 ChaCha20-Poly1305 for Windows/.NET builds whose platform
    /// cryptography provider may not expose the algorithm.
    /// </summary>
    public sealed class ChaCha20Poly1305Compat : IDisposable
    {
        private readonly byte[] key;

        public ChaCha20Poly1305Compat(byte[] key)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("ChaCha20 key must be 32 bytes");
            this.key = (byte[])key.Clone();
        }

        public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
        {
            PortableChaCha20Poly1305.Encrypt(key, nonce, plaintext, ciphertext, tag, associatedData);
        }

        public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            PortableChaCha20Poly1305.Decrypt(key, nonce, ciphertext, tag, plaintext, associatedData);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static class PortableChaCha20Poly1305
    {
        private static readonly uint[] Constants =
        {
            0x61707865, 0x3320646e, 0x79622d32, 0x6b206574
        };

        public static void Encrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
        {
            byte[] polyKey = new byte[32];
            byte[] block = new byte[64];
            ChaChaBlock(key, nonce, 0, block);
            Buffer.BlockCopy(block, 0, polyKey, 0, 32);
            Xor(key, nonce, 1, plaintext, ciphertext);
            ComputeTag(polyKey, associatedData, ciphertext, tag);
        }

        public static void Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            byte[] polyKey = new byte[32];
            byte[] block = new byte[64];
            ChaChaBlock(key, nonce, 0, block);
            Buffer.BlockCopy(block, 0, polyKey, 0, 32);
            Span<byte> expected = stackalloc byte[16];
            ComputeTag(polyKey, associatedData, ciphertext, expected);
            bool valid = CryptographicOperations.FixedTimeEquals(expected, tag);
            if (!valid) throw new CryptographicException("authentication tag mismatch");
            Xor(key, nonce, 1, ciphertext, plaintext);
        }

        private static void Xor(byte[] key, ReadOnlySpan<byte> nonce, uint counter,
            ReadOnlySpan<byte> input, Span<byte> output)
        {
            byte[] block = new byte[64];
            int offset = 0;
            while (offset < input.Length)
            {
                ChaChaBlock(key, nonce, counter++, block);
                int take = Math.Min(64, input.Length - offset);
                for (int i = 0; i < take; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
                offset += take;
            }
        }

        private static void ChaChaBlock(byte[] key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
        {
            uint[] state = new uint[16];
            uint[] working = new uint[16];
            state[0] = Constants[0];
            state[1] = Constants[1];
            state[2] = Constants[2];
            state[3] = Constants[3];
            for (int i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
            state[12] = counter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));
            Array.Copy(state, working, 16);

            for (int round = 0; round < 10; round++)
            {
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), working[i] + state[i]);
        }

        private static void QuarterRound(uint[] state, int a, int b, int c, int d)
        {
            state[a] += state[b]; state[d] = RotateLeft(state[d] ^ state[a], 16);
            state[c] += state[d]; state[b] = RotateLeft(state[b] ^ state[c], 12);
            state[a] += state[b]; state[d] = RotateLeft(state[d] ^ state[a], 8);
            state[c] += state[d]; state[b] = RotateLeft(state[b] ^ state[c], 7);
        }

        private static uint RotateLeft(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }

        private static void ComputeTag(ReadOnlySpan<byte> polyKey, ReadOnlySpan<byte> associatedData,
            ReadOnlySpan<byte> ciphertext, Span<byte> tag)
        {
            byte[] mac = new byte[associatedData.Length + PadLength(associatedData.Length) +
                ciphertext.Length + PadLength(ciphertext.Length) + 16];
            int offset = 0;
            associatedData.CopyTo(mac.AsSpan(offset));
            offset += associatedData.Length + PadLength(associatedData.Length);
            ciphertext.CopyTo(mac.AsSpan(offset));
            offset += ciphertext.Length + PadLength(ciphertext.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(mac.AsSpan(offset), (ulong)associatedData.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(mac.AsSpan(offset + 8), (ulong)ciphertext.Length);

            byte[] rBytes = polyKey.Slice(0, 16).ToArray();
            rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
            rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;
            BigInteger r = PositiveLittleEndian(rBytes);
            BigInteger accumulator = BigInteger.Zero;
            BigInteger modulus = (BigInteger.One << 130) - 5;
            for (int position = 0; position < mac.Length; position += 16)
            {
                int length = Math.Min(16, mac.Length - position);
                byte[] block = new byte[length + 1];
                Buffer.BlockCopy(mac, position, block, 0, length);
                block[length] = 1;
                accumulator = ((accumulator + PositiveLittleEndian(block)) * r) % modulus;
            }

            BigInteger s = PositiveLittleEndian(polyKey.Slice(16, 16).ToArray());
            BigInteger result = (accumulator + s) & ((BigInteger.One << 128) - 1);
            byte[] resultBytes = result.ToByteArray();
            tag.Clear();
            resultBytes.AsSpan(0, Math.Min(16, resultBytes.Length)).CopyTo(tag);
        }

        private static int PadLength(int length)
        {
            return (16 - (length & 15)) & 15;
        }

        private static BigInteger PositiveLittleEndian(byte[] bytes)
        {
            byte[] positive = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, positive, 0, bytes.Length);
            return new BigInteger(positive);
        }
    }
}
