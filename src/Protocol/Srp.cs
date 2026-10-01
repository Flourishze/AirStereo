using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo.Protocol
{
    /// <summary>
    /// HAP flavour of SRP-6a with SHA-512 and the 3072 bit group, used by /pair-setup.
    /// HAP uses the unpadded generator inside M1 but the padded generator inside k, and
    /// pads both public values inside u.
    /// </summary>
    public static class Srp
    {
        public const int PrimeLength = 384;         // 3072 bits
        public const string DefaultPin = "3939";    // transient pairing PIN

        private const string PrimeHex =
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA6" +
            "3B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
            "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F2411" +
            "7C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
            "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08" +
            "CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
            "DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D" +
            "04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
            "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D8760273" +
            "3EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB31" +
            "43DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

        private static readonly BigInteger N = Hex.ToNumber(Hex.FromHex(PrimeHex));
        private static readonly BigInteger G = new BigInteger(5);

        public sealed class Proof
        {
            public byte[] Public = Array.Empty<byte>();          // A, big endian, unpadded
            public byte[] ClientProof = Array.Empty<byte>();     // M1
            public byte[] ExpectedServer = Array.Empty<byte>();  // M2
            public byte[] SessionKey = Array.Empty<byte>();      // K
        }

        public static byte[] Hash(params byte[][] parts)
        {
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            {
                foreach (byte[] part in parts) hash.AppendData(part);
                return hash.GetHashAndReset();
            }
        }

        private static byte[] Pad(BigInteger value)
        {
            byte[] big = Hex.FromNumber(value);
            return big.Length >= PrimeLength ? big : Hex.FromNumber(value, PrimeLength);
        }

        /// <summary>
        /// Runs the client half of the exchange. <paramref name="privateKey"/> is the random
        /// 32 byte SRP secret, <paramref name="serverPublic"/> comes from the M2 reply.
        /// </summary>
        public static Proof ClientProof(byte[] privateKey, byte[] salt, byte[] serverPublic, string pin)
        {
            if (salt == null || salt.Length == 0 || salt.Length > 64)
            {
                throw new ProtocolException("invalid SRP salt");
            }
            if (serverPublic == null || serverPublic.Length == 0 || serverPublic.Length > PrimeLength)
            {
                throw new ProtocolException("invalid SRP server public value");
            }

            BigInteger serverValue = Hex.ToNumber(serverPublic);
            if (serverValue % N == 0) throw new ProtocolException("invalid SRP server public value");

            BigInteger a = Hex.ToNumber(privateKey);
            BigInteger publicValue = BigInteger.ModPow(G, a, N);
            byte[] publicBytes = Hex.FromNumber(publicValue);

            BigInteger k = Hex.ToNumber(Hash(Hex.FromNumber(N), Pad(G)));
            byte[] pinHash = Hash(Encoding.UTF8.GetBytes("Pair-Setup:"), Encoding.UTF8.GetBytes(pin));
            BigInteger x = Hex.ToNumber(Hash(salt, pinHash));
            BigInteger u = Hex.ToNumber(Hash(Pad(publicValue), Pad(serverValue)));
            if (u.IsZero) throw new ProtocolException("invalid SRP scrambling parameter");

            BigInteger kgx = k * BigInteger.ModPow(G, x, N) % N;
            BigInteger basis = (serverValue - kgx + N) % N;
            BigInteger session = BigInteger.ModPow(basis, a + u * x, N);

            byte[] sessionKey = Hash(Hex.FromNumber(session));
            byte[] hashN = Hash(Hex.FromNumber(N));
            byte[] hashG = Hash(Hex.FromNumber(G));
            byte[] xor = new byte[hashN.Length];
            for (int i = 0; i < xor.Length; i++) xor[i] = (byte)(hashN[i] ^ hashG[i]);

            Proof proof = new Proof();
            proof.Public = publicBytes;
            proof.ClientProof = Hash(xor, Hash(Encoding.UTF8.GetBytes("Pair-Setup")),
                salt, publicBytes, serverPublic, sessionKey);
            proof.ExpectedServer = Hash(publicBytes, proof.ClientProof, sessionKey);
            proof.SessionKey = sessionKey;
            return proof;
        }
    }
}
