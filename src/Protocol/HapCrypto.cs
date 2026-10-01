using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo.Protocol
{
    /// <summary>
    /// Key derivation and AEAD helpers shared by HAP pairing and the AirPlay control channel.
    /// HKDF-SHA512 and ChaCha20-Poly1305 helpers. ChaCha20-Poly1305 transparently falls
    /// back to the managed implementation when the operating system lacks the primitive.
    /// </summary>
    public static class HapCrypto
    {
        public const int KeySize = 32;

        public static byte[] Derive(byte[] secret, string salt, string info)
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA512,
                secret,
                KeySize,
                Encoding.UTF8.GetBytes(salt),
                Encoding.UTF8.GetBytes(info));
        }

        /// <summary>Nonce for the fixed-label pairing messages: four zero bytes then the label.</summary>
        public static byte[] LabelNonce(string label)
        {
            if (label.Length != 8) throw new ArgumentException("HAP nonce labels are eight characters");
            byte[] nonce = new byte[12];
            Encoding.ASCII.GetBytes(label, 0, 8, nonce, 4);
            return nonce;
        }

        public static byte[] Seal(byte[] key, string label, byte[] plaintext)
        {
            return Seal(key, label, plaintext, null);
        }

        public static byte[] Seal(byte[] key, string label, byte[] plaintext, byte[] associatedData)
        {
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];
            using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
            {
                aead.Encrypt(LabelNonce(label), plaintext, ciphertext, tag,
                    associatedData == null ? ReadOnlySpan<byte>.Empty : associatedData);
            }
            return Concat(ciphertext, tag);
        }

        /// <summary>Returns null when the tag does not authenticate.</summary>
        public static byte[] Open(byte[] key, string label, byte[] ciphertextAndTag)
        {
            return Open(key, label, ciphertextAndTag, null);
        }

        public static byte[] Open(byte[] key, string label, byte[] ciphertextAndTag, byte[] associatedData)
        {
            if (ciphertextAndTag.Length < 16) return null;
            int length = ciphertextAndTag.Length - 16;
            byte[] plaintext = new byte[length];
            try
            {
                using (ChaCha20Poly1305Compat aead = new ChaCha20Poly1305Compat(key))
                {
                    aead.Decrypt(
                        LabelNonce(label),
                        ciphertextAndTag.AsSpan(0, length),
                        ciphertextAndTag.AsSpan(length, 16),
                        plaintext,
                        associatedData == null ? ReadOnlySpan<byte>.Empty : associatedData);
                }
            }
            catch (CryptographicException)
            {
                return null;
            }
            return plaintext;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int size = 0;
            foreach (byte[] part in parts) size += part.Length;
            byte[] result = new byte[size];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }
    }

    /// <summary>
    /// The HAP record layer that encrypts the AirPlay control channel: each record is
    /// [u16 little endian length][ciphertext][16 byte tag], the length bytes double as
    /// associated data, and the nonce is four zero bytes followed by a little endian
    /// counter that starts at zero and never repeats.
    /// </summary>
    public sealed class RecordCipher : IDisposable
    {
        public const int MaxRecord = 1024;

        private readonly ChaCha20Poly1305Compat aead;
        private ulong counter;

        public RecordCipher(byte[] key)
        {
            if (key == null || key.Length != HapCrypto.KeySize)
            {
                throw new ArgumentException("record cipher needs a 32 byte key");
            }
            aead = new ChaCha20Poly1305Compat(key);
        }

        public ulong Counter { get { return counter; } }

        private void Nonce(Span<byte> destination)
        {
            destination.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(4), counter);
        }

        public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            byte[] output = new byte[plaintext.Length + 16];
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce);
            aead.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length),
                output.AsSpan(plaintext.Length, 16), associatedData);
            counter++;
            return output;
        }

        public byte[] Open(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> associatedData)
        {
            if (ciphertextAndTag.Length < 16) throw new ProtocolException("truncated encrypted record");
            int length = ciphertextAndTag.Length - 16;
            byte[] plaintext = new byte[length];
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce);
            try
            {
                aead.Decrypt(nonce, ciphertextAndTag.Slice(0, length),
                    ciphertextAndTag.Slice(length, 16), plaintext, associatedData);
            }
            catch (CryptographicException error)
            {
                throw new ProtocolException("control channel record failed authentication", error);
            }
            counter++;
            return plaintext;
        }

        /// <summary>Frames an arbitrary byte stream into at most 1024 byte records.</summary>
        public byte[] Frame(byte[] data)
        {
            byte[] output = new byte[data.Length + (data.Length / MaxRecord + 1) * 18];
            int written = 0;
            int offset = 0;
            while (offset < data.Length)
            {
                int take = Math.Min(MaxRecord, data.Length - offset);
                Span<byte> length = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)take);
                byte[] sealedRecord = Seal(data.AsSpan(offset, take), length);
                length.CopyTo(output.AsSpan(written));
                Buffer.BlockCopy(sealedRecord, 0, output, written + 2, sealedRecord.Length);
                written += 2 + sealedRecord.Length;
                offset += take;
            }
            return output.Length == written ? output : output.AsSpan(0, written).ToArray();
        }

        public void Dispose()
        {
            aead.Dispose();
        }
    }
}
