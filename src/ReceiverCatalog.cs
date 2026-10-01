using System;
using System.Collections.Generic;

namespace AirStereo
{
    /// <summary>Turns raw mDNS records into receivers and groups them into logical targets.</summary>
    public static class ReceiverCatalog
    {
        public static readonly string[] ServiceTypes = new string[]
        {
            "_airplay._tcp.local",
            "_raop._tcp.local"
        };

        public static List<Receiver> Build(List<MdnsRecord> records)
        {
            Dictionary<string, Receiver> receivers = new Dictionary<string, Receiver>();
            Dictionary<string, string> hostAddresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (MdnsRecord record in records)
            {
                if (record.Type == MdnsRecord.TypeA && record.Text.Length > 0)
                {
                    hostAddresses[record.Name.ToLowerInvariant()] = record.Text;
                }
            }

            foreach (MdnsRecord record in records)
            {
                if (record.Type != MdnsRecord.TypeSrv) continue;
                string service = MatchService(record.Name);
                if (service == null) continue;

                Receiver receiver = GetOrCreate(receivers, record.Name);
                receiver.ServiceType = service;
                int colon = record.Text.LastIndexOf(':');
                if (colon <= 0) continue;
                receiver.Host = record.Text.Substring(0, colon);
                int port;
                if (int.TryParse(record.Text.Substring(colon + 1), out port)) receiver.Port = port;

                string address;
                if (hostAddresses.TryGetValue(receiver.Host.ToLowerInvariant(), out address))
                {
                    receiver.Address = address;
                }
            }

            foreach (MdnsRecord record in records)
            {
                if (record.Type != MdnsRecord.TypeTxt) continue;
                string service = MatchService(record.Name);
                if (service == null) continue;

                Receiver receiver = GetOrCreate(receivers, record.Name);
                receiver.ServiceType = service;
                // A TXT record is a complete snapshot. Newer group/leader information must
                // replace the older round, including keys removed when a pair is dissolved.
                receiver.Txt = TxtRecord.Parse(record.Text);
            }

            List<Receiver> ordered = new List<Receiver>(receivers.Values);
            // Prefer AirPlay's port/metadata, merging the RAOP advertisement by physical
            // identity. Display names are not unique and must never be a merge key.
            ordered.Sort((left, right) => string.Compare(left.ServiceType, right.ServiceType, StringComparison.OrdinalIgnoreCase));
            List<Receiver> endpoints = new List<Receiver>();
            foreach (Receiver candidate in ordered)
            {
                if (candidate.Address.Length == 0 || candidate.Port <= 0) continue;
                Receiver match = endpoints.Find(existing =>
                    (!string.IsNullOrEmpty(existing.DeviceId) && !string.IsNullOrEmpty(candidate.DeviceId))
                        ? string.Equals(existing.DeviceId, candidate.DeviceId, StringComparison.OrdinalIgnoreCase)
                        : existing.Host.Length > 0 && string.Equals(existing.Host, candidate.Host, StringComparison.OrdinalIgnoreCase));
                if (match == null) endpoints.Add(candidate);
                else
                {
                    if (string.IsNullOrEmpty(match.DeviceId)) match.ServiceDeviceId = candidate.DeviceId ?? "";
                    match.Txt.AddFrom(candidate.Txt);
                }
            }
            ordered = endpoints;
            ordered.Sort(delegate (Receiver left, Receiver right)
            {
                return string.Compare(left.Instance, right.Instance, StringComparison.OrdinalIgnoreCase);
            });
            return ordered;
        }

        private static Receiver GetOrCreate(Dictionary<string, Receiver> receivers, string serviceInstance)
        {
            string instance = InstanceName(serviceInstance);
            string key = serviceInstance.ToLowerInvariant();
            Receiver receiver;
            if (receivers.TryGetValue(key, out receiver)) return receiver;

            receiver = new Receiver();
            receiver.Instance = instance;
            int at = serviceInstance.IndexOf('@');
            if (at == 12)
            {
                string mac = serviceInstance.Substring(0, at);
                bool valid = true;
                foreach (char digit in mac) if (!Uri.IsHexDigit(digit)) valid = false;
                if (valid) receiver.ServiceDeviceId =
                    string.Join(":", new[] { mac.Substring(0, 2), mac.Substring(2, 2), mac.Substring(4, 2),
                        mac.Substring(6, 2), mac.Substring(8, 2), mac.Substring(10, 2) }).ToUpperInvariant();
            }
            receivers[key] = receiver;
            return receiver;
        }

        private static string InstanceName(string serviceInstance)
        {
            string name = serviceInstance;
            string[] suffixes = new string[]
            {
                "._airplay._tcp.local",
                "._raop._tcp.local"
            };
            foreach (string suffix in suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }
            int at = name.IndexOf('@');        // RAOP instances look like 42D250C50196@卧室
            if (at >= 0) name = name.Substring(at + 1);
            return name;
        }

        private static string MatchService(string name)
        {
            foreach (string service in ServiceTypes)
            {
                if (name.EndsWith(service, StringComparison.OrdinalIgnoreCase)) return service;
            }
            return null;
        }

        /// <summary>
        /// Prefer an explicitly advertised tight-sync pair identity (tsm=1 + tsid). Modern
        /// HomePods may share tsid while advertising different transient gid suffixes.
        /// Older receivers without this metadata continue to use an exact shared gid.
        /// </summary>
        public static List<ReceiverGroup> Group(List<Receiver> receivers)
        {
            List<ReceiverGroup> groups = new List<ReceiverGroup>();
            Dictionary<string, ReceiverGroup> byGroupId = new Dictionary<string, ReceiverGroup>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Receiver receiver in receivers)
            {
                if (!identities.Add(receiver.Identity)) continue;
                string pairId = receiver.StereoPairId;
                string groupId = pairId.Length > 0 ? pairId : receiver.GroupId;
                if (string.IsNullOrEmpty(groupId))
                {
                    ReceiverGroup single = new ReceiverGroup();
                    single.Name = receiver.Instance;
                    single.Members.Add(receiver);
                    groups.Add(single);
                    continue;
                }

                ReceiverGroup group;
                string catalogKey = (pairId.Length > 0 ? "stereo:" : "group:") + groupId;
                if (!byGroupId.TryGetValue(catalogKey, out group))
                {
                    group = new ReceiverGroup();
                    group.Name = receiver.Instance;
                    group.GroupId = groupId;
                    group.StereoPairId = pairId;
                    byGroupId[catalogKey] = group;
                    groups.Add(group);
                }
                if (!string.IsNullOrEmpty(receiver.GroupName)) group.Name = receiver.GroupName;
                group.Members.Add(receiver);
            }

            return MergeInferredPairs(groups);
        }

        /// <summary>
        /// Multicast packet loss occasionally drops the TXT record that carries the group id.
        /// A name such as "X" and "X (2)" is useful as a warning, but it is not proof of a
        /// native pair. Keep both physical receivers as separate selectable rows and annotate
        /// each one with the common name instead of merging them into one route.
        /// </summary>
        public static List<ReceiverGroup> MergeInferredPairs(List<ReceiverGroup> groups)
        {
            List<ReceiverGroup> merged = new List<ReceiverGroup>();
            List<ReceiverGroup> singles = new List<ReceiverGroup>();
            foreach (ReceiverGroup group in groups)
            {
                if (group.GroupId.Length == 0 && group.Members.Count == 1) singles.Add(group);
                else merged.Add(group);
            }

            for (int i = 0; i < singles.Count; i++)
            {
                int firstIndex;
                string firstName = PairBaseName(singles[i].Members[0].Instance, out firstIndex);
                for (int j = i + 1; j < singles.Count; j++)
                {
                    int secondIndex;
                    string secondName = PairBaseName(singles[j].Members[0].Instance, out secondIndex);
                    if (!string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (firstIndex == secondIndex) continue;
                    if (Math.Min(firstIndex, secondIndex) != 1) continue;

                    singles[i].Inferred = true;
                    singles[i].InferredPairName = firstName;
                    singles[j].Inferred = true;
                    singles[j].InferredPairName = firstName;
                    break;
                }
            }

            merged.AddRange(singles);
            return merged;
        }

        /// <summary>
        /// Splits "卧室 (2)" into the base name "卧室" and the ordinal 2. HomePod stereo pairs
        /// may name their halves "X" and "X (2)"; the suffix is only a discovery hint.
        /// </summary>
        private static string PairBaseName(string instance, out int index)
        {
            index = 1;
            string name = instance ?? "";
            if (!name.EndsWith(")", StringComparison.Ordinal)) return name;

            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= 0) return name;

            string digits = name.Substring(open + 2, name.Length - open - 3);
            int value;
            if (digits.Length == 0 || digits.Length > 2 || !int.TryParse(digits, out value) || value < 2)
            {
                return name;
            }
            index = value;
            return name.Substring(0, open);
        }
    }
}
