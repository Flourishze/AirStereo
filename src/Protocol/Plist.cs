using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AirStereo.Protocol
{
    /// <summary>
    /// Binary property list reader and writer. Only the object types AirPlay uses are
    /// implemented: dictionaries, arrays, strings, integers, reals, booleans and data.
    /// </summary>
    public static class Plist
    {
        private sealed class Entry
        {
            public object Value;
            public List<int> KeyRefs;
            public List<int> ValueRefs;
        }

        public static byte[] Write(object value)
        {
            List<Entry> table = new List<Entry>();
            int top = Flatten(value, table);

            int referenceSize = ReferenceSize(table.Count);
            List<byte[]> blobs = new List<byte[]>(table.Count);
            foreach (Entry entry in table) blobs.Add(Encode(entry, referenceSize));

            int bodySize = 8;
            foreach (byte[] blob in blobs) bodySize += blob.Length;
            int offsetSize = bodySize < 0x100 ? 1 : (bodySize < 0x10000 ? 2 : 4);

            int offsetTableOffset = bodySize;
            int total = offsetTableOffset + table.Count * offsetSize + 32;
            byte[] output = new byte[total];
            Encoding.ASCII.GetBytes("bplist00").CopyTo(output, 0);

            int position = 8;
            int[] offsets = new int[table.Count];
            for (int i = 0; i < blobs.Count; i++)
            {
                offsets[i] = position;
                Buffer.BlockCopy(blobs[i], 0, output, position, blobs[i].Length);
                position += blobs[i].Length;
            }

            for (int i = 0; i < offsets.Length; i++)
            {
                WriteSizedInteger(output, offsetTableOffset + i * offsetSize, (ulong)offsets[i], offsetSize);
            }

            int trailer = total - 32;
            output[trailer + 6] = (byte)offsetSize;
            output[trailer + 7] = (byte)referenceSize;
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(trailer + 8), (ulong)table.Count);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(trailer + 16), (ulong)top);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(trailer + 24), (ulong)offsetTableOffset);
            return output;
        }

        private static int ReferenceSize(int count)
        {
            if (count < 0x100) return 1;
            return count < 0x10000 ? 2 : 4;
        }

        private static int Flatten(object value, List<Entry> table)
        {
            Entry entry = new Entry();
            table.Add(entry);
            int index = table.Count - 1;

            if (value is Dictionary<string, object> dictionary)
            {
                entry.KeyRefs = new List<int>(dictionary.Count);
                entry.ValueRefs = new List<int>(dictionary.Count);
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    entry.KeyRefs.Add(Flatten(pair.Key, table));
                    entry.ValueRefs.Add(Flatten(pair.Value ?? "", table));
                }
                return index;
            }

            if (value is List<object> list)
            {
                entry.ValueRefs = new List<int>(list.Count);
                foreach (object item in list) entry.ValueRefs.Add(Flatten(item ?? "", table));
                return index;
            }

            if (value is object[] array)
            {
                entry.ValueRefs = new List<int>(array.Length);
                foreach (object item in array) entry.ValueRefs.Add(Flatten(item ?? "", table));
                return index;
            }

            entry.Value = value;
            return index;
        }

        private static byte[] Encode(Entry entry, int referenceSize)
        {
            if (entry.ValueRefs != null)
            {
                bool isDictionary = entry.KeyRefs != null;
                int count = entry.ValueRefs.Count;
                List<byte> output = new List<byte>();
                output.AddRange(MarkerWithLength(isDictionary ? (byte)0xD0 : (byte)0xA0, count));
                foreach (int key in entry.KeyRefs ?? new List<int>())
                {
                    output.AddRange(SizedBytes((ulong)key, referenceSize));
                }
                foreach (int childReference in entry.ValueRefs)
                {
                    output.AddRange(SizedBytes((ulong)childReference, referenceSize));
                }
                return output.ToArray();
            }

            object value = entry.Value;
            if (value is string text)
            {
                byte[] ascii = Encoding.ASCII.GetBytes(text);
                bool plain = ascii.Length == text.Length && text.IndexOf('\u0000') < 0;
                byte[] payload = plain ? ascii : Encoding.BigEndianUnicode.GetBytes(text);
                int characters = plain ? text.Length : text.Length;
                List<byte> output = new List<byte>();
                output.AddRange(MarkerWithLength(plain ? (byte)0x50 : (byte)0x60, characters));
                output.AddRange(payload);
                return output.ToArray();
            }
            if (value is bool flag)
            {
                return new byte[] { flag ? (byte)0x09 : (byte)0x08 };
            }
            if (value is byte[] data)
            {
                List<byte> output = new List<byte>();
                output.AddRange(MarkerWithLength(0x40, data.Length));
                output.AddRange(data);
                return output.ToArray();
            }
            if (value is double real)
            {
                byte[] payload = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(payload, BitConverter.DoubleToUInt64Bits(real));
                return HapCrypto.Concat(new byte[] { 0x23 }, payload);
            }

            ulong number;
            if (value is int small) number = (ulong)small;
            else if (value is long large) number = (ulong)large;
            else if (value is uint unsigned) number = unsigned;
            else if (value is byte tiny) number = tiny;
            else if (value is null) return new byte[] { 0x00 };
            else throw new ProtocolException("cannot encode " + value.GetType().Name + " into a plist");

            return EncodeInteger(number);
        }

        private static byte[] MarkerWithLength(byte marker, int length)
        {
            if (length < 15) return new byte[] { (byte)(marker | length) };
            return HapCrypto.Concat(new byte[] { (byte)(marker | 0x0F) }, EncodeInteger((ulong)length));
        }

        private static byte[] EncodeInteger(ulong value)
        {
            if (value < 0x100) return new byte[] { 0x10, (byte)value };
            if (value < 0x10000) return HapCrypto.Concat(new byte[] { 0x11 }, SizedBytes(value, 2));
            if (value <= uint.MaxValue) return HapCrypto.Concat(new byte[] { 0x12 }, SizedBytes(value, 4));
            return HapCrypto.Concat(new byte[] { 0x13 }, SizedBytes(value, 8));
        }

        private static byte[] SizedBytes(ulong value, int size)
        {
            byte[] bytes = new byte[size];
            for (int i = size - 1; i >= 0; i--)
            {
                bytes[i] = (byte)value;
                value >>= 8;
            }
            return bytes;
        }

        private static void WriteSizedInteger(byte[] output, int offset, ulong value, int size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                output[offset + i] = (byte)value;
                value >>= 8;
            }
        }

        public static object Read(byte[] data)
        {
            if (data == null || data.Length < 40 || Encoding.ASCII.GetString(data, 0, 8) != "bplist00")
            {
                throw new ProtocolException("response is not a binary plist");
            }

            int trailer = data.Length - 32;
            int offsetSize = data[trailer + 6];
            int referenceSize = data[trailer + 7];
            long count = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 8));
            long top = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 16));
            long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 24));
            if (offsetSize < 1 || offsetSize > 8 || referenceSize < 1 || referenceSize > 8 ||
                count < 0 || count > 1_000_000)
            {
                throw new ProtocolException("unusable plist trailer");
            }
            if (tableOffset + count * offsetSize > data.Length) throw new ProtocolException("plist offset table is out of range");

            long[] offsets = new long[count];
            for (long i = 0; i < count; i++)
            {
                offsets[i] = (long)ReadSizedInteger(data, (int)(tableOffset + i * offsetSize), offsetSize);
            }

            return ReadObject(data, offsets, top, referenceSize, 0);
        }

        private static object ReadObject(byte[] data, long[] offsets, long reference, int referenceSize, int depth)
        {
            if (depth > 64) throw new ProtocolException("plist nesting is too deep");
            if (reference < 0 || reference >= offsets.Length) throw new ProtocolException("plist object reference out of range");

            int position = (int)offsets[reference];
            if (position < 0 || position >= data.Length) throw new ProtocolException("plist object offset out of range");
            byte marker = data[position++];
            int kind = marker >> 4;

            switch (kind)
            {
                case 0x0:
                    switch (marker & 0x0F)
                    {
                        case 0x8: return false;
                        case 0x9: return true;
                        default: return null;
                    }
                case 0x1:
                {
                    int size = 1 << (marker & 0x0F);
                    return (long)ReadSizedInteger(data, position, size);
                }
                case 0x2:
                {
                    int size = 1 << (marker & 0x0F);
                    ulong raw = ReadSizedInteger(data, position, size);
                    return size == 4 ? BitConverter.UInt32BitsToSingle((uint)raw) : BitConverter.UInt64BitsToDouble(raw);
                }
                case 0x4:
                {
                    int length = ReadLength(data, ref position, marker);
                    byte[] payload = new byte[length];
                    Buffer.BlockCopy(data, position, payload, 0, length);
                    return payload;
                }
                case 0x5:
                {
                    int length = ReadLength(data, ref position, marker);
                    return length == 0 ? "" : Encoding.ASCII.GetString(data, position, length);
                }
                case 0x6:
                {
                    int length = ReadLength(data, ref position, marker);
                    return length == 0 ? "" : Encoding.BigEndianUnicode.GetString(data, position, length * 2);
                }
                case 0x8:
                    return (long)ReadSizedInteger(data, position, (marker & 0x0F) + 1);
                case 0xA:
                case 0xC:
                {
                    int length = ReadLength(data, ref position, marker);
                    List<object> items = new List<object>(length);
                    for (int i = 0; i < length; i++)
                    {
                        long item = (long)ReadSizedInteger(data, position + i * referenceSize, referenceSize);
                        items.Add(ReadObject(data, offsets, item, referenceSize, depth + 1));
                    }
                    return items;
                }
                case 0xD:
                {
                    int length = ReadLength(data, ref position, marker);
                    Dictionary<string, object> items = new Dictionary<string, object>(length);
                    for (int i = 0; i < length; i++)
                    {
                        long keyReference = (long)ReadSizedInteger(data, position + i * referenceSize, referenceSize);
                        long valueReference = (long)ReadSizedInteger(data, position + (length + i) * referenceSize, referenceSize);
                        object key = ReadObject(data, offsets, keyReference, referenceSize, depth + 1);
                        object value = ReadObject(data, offsets, valueReference, referenceSize, depth + 1);
                        items[Convert.ToString(key, CultureInfo.InvariantCulture) ?? ""] = value;
                    }
                    return items;
                }
                default:
                    throw new ProtocolException("unsupported plist marker 0x" + marker.ToString("x2"));
            }
        }

        /// <summary>Reads the length that follows a marker nibble of 15, which is itself an integer object.</summary>
        private static int ReadLength(byte[] data, ref int position, byte marker)
        {
            int inlineLength = marker & 0x0F;
            if (inlineLength != 0x0F) return inlineLength;

            byte lengthMarker = data[position];
            if ((lengthMarker >> 4) != 0x1) throw new ProtocolException("plist collection length is not an integer");
            int size = 1 << (lengthMarker & 0x0F);
            long value = (long)ReadSizedInteger(data, position + 1, size);
            position += 1 + size;
            if (value < 0 || value > 4_000_000) throw new ProtocolException("plist collection is too large");
            return (int)value;
        }

        private static ulong ReadSizedInteger(byte[] data, int position, int size)
        {
            if (size < 1 || size > 8 || position < 0 || position + size > data.Length)
            {
                throw new ProtocolException("plist integer is out of range");
            }
            ulong value = 0;
            for (int i = 0; i < size; i++) value = (value << 8) | data[position + i];
            return value;
        }

        public static Dictionary<string, object> AsDictionary(object value)
        {
            if (value is Dictionary<string, object> dictionary) return dictionary;
            throw new ProtocolException("expected a plist dictionary");
        }

        public static List<object> AsList(object value)
        {
            if (value is List<object> list) return list;
            throw new ProtocolException("expected a plist array");
        }

        public static string Text(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary.TryGetValue(key, out object value))
            {
                if (value is string text) return text;
                if (value is long number) return number.ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }

        public static long? Integer(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary.TryGetValue(key, out object value) && value is long number) return number;
            return null;
        }

        public static string Describe(object value, int depth = 0)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                StringBuilder text = new StringBuilder("{");
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (text.Length > 1) text.Append(", ");
                    text.Append(pair.Key).Append('=').Append(Describe(pair.Value, depth + 1));
                }
                return text.Append('}').ToString();
            }
            if (value is List<object> list)
            {
                StringBuilder text = new StringBuilder("[");
                foreach (object item in list)
                {
                    if (text.Length > 1) text.Append(", ");
                    text.Append(Describe(item, depth + 1));
                }
                return text.Append(']').ToString();
            }
            if (value is byte[] data) return data.Length + " bytes";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }
    }
}
