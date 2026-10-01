using System;
using System.Collections.Generic;

namespace AirStereo
{
    /// <summary>One resource record decoded from an mDNS response packet.</summary>
    public sealed class MdnsRecord
    {
        public const ushort TypeA = 1;
        public const ushort TypePtr = 12;
        public const ushort TypeTxt = 16;
        public const ushort TypeAaaa = 28;
        public const ushort TypeSrv = 33;

        public string Name = "";
        public ushort Type;
        public string Text = "";
        public string SourceAddress = "";

        public bool IsService(string service)
        {
            return Name.EndsWith(service, StringComparison.OrdinalIgnoreCase);
        }

        public string TypeLabel
        {
            get
            {
                if (Type == TypeA) return "A";
                if (Type == TypeAaaa) return "AAAA";
                if (Type == TypePtr) return "PTR";
                if (Type == TypeSrv) return "SRV";
                if (Type == TypeTxt) return "TXT";
                return "T" + Type;
            }
        }
    }

    /// <summary>TXT record content, split into key=value pairs.</summary>
    public sealed class TxtRecord
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string RawText = "";

        public static TxtRecord Parse(string text)
        {
            TxtRecord txt = new TxtRecord();
            if (text == null) return txt;
            txt.RawText = text;
            foreach (string part in text.Split(' '))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) txt._values[part] = "";
                else txt._values[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return txt;
        }

        /// <summary>Merges fields from another instance record; the first value seen wins.</summary>
        public void AddFrom(TxtRecord other)
        {
            foreach (KeyValuePair<string, string> pair in other._values)
            {
                if (!_values.ContainsKey(pair.Key)) _values[pair.Key] = pair.Value;
            }
            if (other.RawText.Length > 0)
            {
                RawText = RawText.Length == 0 ? other.RawText : RawText + " " + other.RawText;
            }
        }

        public string Get(string key)
        {
            string value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        public int GetInt(string key, int fallback)
        {
            string value = Get(key);
            if (value == null) return fallback;
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        public bool Has(string key)
        {
            return _values.ContainsKey(key);
        }

        public IEnumerable<KeyValuePair<string, string>> Items
        {
            get { return _values; }
        }
    }
}
