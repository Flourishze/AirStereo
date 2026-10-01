using System;
using System.Collections.Generic;
using System.Text;

namespace AirStereo.Protocol
{
    /// <summary>
    /// TLV8 encoding used by HAP pairing. Values longer than 255 bytes are split into
    /// consecutive chunks that share a tag; decoding joins them back together.
    /// </summary>
    public static class Tlv
    {
        public const byte ErrorTag = 7;

        public static byte[] Encode(params (byte Tag, byte[] Value)[] items)
        {
            List<byte> output = new List<byte>();
            foreach ((byte tag, byte[] value) in items)
            {
                if (value == null || value.Length == 0)
                {
                    output.Add(tag);
                    output.Add(0);
                    continue;
                }

                int offset = 0;
                while (offset < value.Length)
                {
                    int take = Math.Min(255, value.Length - offset);
                    output.Add(tag);
                    output.Add((byte)take);
                    for (int i = 0; i < take; i++) output.Add(value[offset + i]);
                    offset += take;
                }
            }
            return output.ToArray();
        }

        public static Dictionary<byte, byte[]> Decode(byte[] data)
        {
            Dictionary<byte, byte[]> values = new Dictionary<byte, byte[]>();
            List<byte>[] chunks = new List<byte>[256];
            int position = 0;

            while (position < data.Length)
            {
                if (position + 2 > data.Length) throw new ProtocolException("truncated TLV header");
                byte tag = data[position];
                int length = data[position + 1];
                position += 2;
                if (position + length > data.Length) throw new ProtocolException("truncated TLV value");

                if (chunks[tag] == null) chunks[tag] = new List<byte>();
                for (int i = 0; i < length; i++) chunks[tag].Add(data[position + i]);
                position += length;
            }

            for (int tag = 0; tag < chunks.Length; tag++)
            {
                if (chunks[tag] != null) values[(byte)tag] = chunks[tag].ToArray();
            }

            if (values.TryGetValue(ErrorTag, out byte[] error) && error.Length > 0)
            {
                throw new ProtocolException("accessory rejected pairing with TLV error " + error[0]);
            }
            return values;
        }

        public static byte[] Required(Dictionary<byte, byte[]> values, byte tag, string name)
        {
            if (!values.TryGetValue(tag, out byte[] value) || value.Length == 0)
            {
                throw new ProtocolException("pairing reply is missing " + name);
            }
            return value;
        }

        public static string Describe(Dictionary<byte, byte[]> values)
        {
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<byte, byte[]> pair in values)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(pair.Key).Append('=').Append(pair.Value.Length).Append("B");
            }
            return text.ToString();
        }
    }

    /// <summary>Raised when a receiver answers with something the protocol does not allow.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
