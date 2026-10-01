using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AirStereo.Protocol
{
    /// <summary>
    /// HAP transient pairing. The receiver shows no PIN prompt for this mode: the shared
    /// PIN is the fixed string 3939 and the session dies with the TCP connection, so no
    /// long lived controller credentials have to be stored on disk.
    /// </summary>
    public static class Pairing
    {
        public static byte[] Transient(RtspConnection connection, Action<string> log = null)
        {
            return Transient(connection, Srp.DefaultPin, log);
        }

        public static byte[] Transient(RtspConnection connection, string pin, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length < 4 || pin.Length > 8)
            {
                throw new ProtocolException("pairing PIN must be four to eight digits");
            }

            byte[][] headers = new byte[][]
            {
                Encoding.ASCII.GetBytes("X-Apple-HKP: 4"),
                Encoding.ASCII.GetBytes("Content-Type: application/octet-stream")
            };

            log?.Invoke("requesting transient pairing");
            connection.Send("POST", "/pair-pin-start", headers, null);

            byte[] m1 = Tlv.Encode(
                (0, new byte[] { 0 }),
                (6, new byte[] { 1 }),
                (0x13, new byte[] { 0x10 }));

            Dictionary<byte, byte[]> m2 = Tlv.Decode(
                connection.Send("POST", "/pair-setup", headers, m1).Body);
            Require(m2, 6, 2, "pairing M2");

            byte[] salt = Tlv.Required(m2, 2, "SRP salt");
            byte[] serverPublic = Tlv.Required(m2, 3, "SRP server public key");

            byte[] privateKey = new byte[32];
            RandomNumberGenerator.Fill(privateKey);
            Srp.Proof proof = Srp.ClientProof(privateKey, salt, serverPublic, pin);
            CryptographicOperations.ZeroMemory(privateKey);

            byte[] m3 = Tlv.Encode(
                (6, new byte[] { 3 }),
                (3, proof.Public),
                (4, proof.ClientProof));

            Dictionary<byte, byte[]> m4 = Tlv.Decode(
                connection.Send("POST", "/pair-setup", headers, m3).Body);
            Require(m4, 6, 4, "pairing M4");

            byte[] serverProof = Tlv.Required(m4, 4, "SRP server proof");
            if (!CryptographicOperations.FixedTimeEquals(serverProof, proof.ExpectedServer))
            {
                throw new ProtocolException("receiver failed to prove knowledge of the PIN");
            }

            log?.Invoke("paired, enabling control channel encryption");
            EnableControlChannel(connection, proof.SessionKey);
            return proof.SessionKey;
        }

        public static void EnableControlChannel(RtspConnection connection, byte[] sessionKey)
        {
            connection.EnableEncryption(
                HapCrypto.Derive(sessionKey, "Control-Salt", "Control-Write-Encryption-Key"),
                HapCrypto.Derive(sessionKey, "Control-Salt", "Control-Read-Encryption-Key"));
        }

        private static void Require(Dictionary<byte, byte[]> values, byte tag, byte expected, string stage)
        {
            byte[] value = Tlv.Required(values, tag, stage);
            if (value.Length != 1 || value[0] != expected)
            {
                throw new ProtocolException(stage + " was not the expected step");
            }
        }
    }
}
