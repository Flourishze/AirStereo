using System;
using System.Collections.Generic;

namespace AirStereo
{
    /// <summary>
    /// One advertised AirPlay endpoint (one physical HomePod), with the TXT fields that
    /// describe group membership.
    /// </summary>
    public sealed class Receiver
    {
        public string Instance = "";        // e.g. "卧室" or "卧室 (2)"
        public string ServiceType = "";     // "_airplay._tcp.local" or "_raop._tcp.local"
        public string Host = "";
        public int Port;
        public string Address = "";
        public TxtRecord Txt = TxtRecord.Parse("");
        public string ServiceDeviceId = "";

        public string DeviceId { get { return Txt.Get("deviceid") ?? ServiceDeviceId; } }
        public string GroupId { get { return Txt.Get("gid"); } }
        /// <summary>Explicit tight-sync pair identity, independent of temporary AirPlay group suffixes.</summary>
        public string StereoPairId
        {
            get
            {
                Guid id;
                return Txt.Get("tsm") == "1" && Guid.TryParse(Txt.Get("tsid"), out id) && id != Guid.Empty
                    ? id.ToString("D").ToUpperInvariant() : "";
            }
        }
        public string GroupName { get { return Txt.Get("gpn"); } }
        public string Model { get { return Txt.Get("model") ?? Txt.Get("am"); } }
        public string OsVersion { get { return Txt.Get("osvers") ?? Txt.Get("ov"); } }
        public string SourceVersion { get { return Txt.Get("srcvers") ?? Txt.Get("vs"); } }
        public string Features { get { return Txt.Get("features") ?? Txt.Get("ft"); } }
        public string Flags { get { return Txt.Get("flags") ?? Txt.Get("sf"); } }
        public string EncryptionTypes { get { return Txt.Get("et"); } }
        public string AccessControl { get { return Txt.Get("acl"); } }

        /// <summary>True when the accessory declares itself the leader of its group.</summary>
        public bool IsGroupLeader
        {
            get
            {
                string flag = Txt.Get("igl");
                return flag == "1";
            }
        }

        /// <summary>True when the accessory is attached to a parent group.</summary>
        public bool HasParentGroup
        {
            get { return Txt.Has("pgid"); }
        }

        public string Key
        {
            get { return Instance + "|" + Address + "|" + Host + ":" + Port; }
        }

        public string Identity
        {
            get { return !string.IsNullOrEmpty(DeviceId) ? DeviceId :
                (!string.IsNullOrEmpty(Host) ? "host:" + Host.ToLowerInvariant() : Key); }
        }

        public override string ToString()
        {
            return Instance + " " + Address + ":" + Port;
        }
    }

    /// <summary>A logical AirPlay receiver: one standalone speaker, or a stereo pair / group.</summary>
    public sealed class ReceiverGroup
    {
        public string Name = "";
        public string GroupId = "";
        public string StereoPairId = "";
        /// <summary>
        /// True when the receiver name resembles another receiver's name, but mDNS did not
        /// announce enough group metadata to confirm a native pair.
        /// </summary>
        public bool Inferred;
        public string InferredPairName = "";
        public List<Receiver> Members = new List<Receiver>();

        /// <summary>
        /// Two members sharing explicit tsid/tsm metadata or a legacy exact group ID are a
        /// confirmed pair. Name hints alone never enter the native route.
        /// </summary>
        public bool IsStereoPair
        {
            get { return Members.Count == 2 && !Inferred && GroupId.Length > 0; }
        }
        public bool IsSuspectedPair { get { return Inferred && InferredPairName.Length > 0; } }
        public bool IsIncompleteGroup
        {
            get { return Members.Count == 1 && GroupId.Length > 0; }
        }
        public bool IsGroup { get { return Members.Count > 1; } }

        public Receiver Leader
        {
            get
            {
                foreach (Receiver member in Members)
                {
                    if (member.IsGroupLeader) return member;
                }
                foreach (Receiver member in Members)
                {
                    if (!member.HasParentGroup) return member;
                }
                return Members.Count > 0 ? Members[0] : null;
            }
        }

        public string Describe()
        {
            string label = Name.Length > 0 ? Name : (Members.Count > 0 ? Members[0].Instance : "?");
            if (!IsGroup) return label;
            return label + (IsStereoPair ? " · stereo pair" : " · group") +
                " (" + Members.Count + "/" + Members.Count + ")" +
                (Inferred ? " · inferred" : "");
        }
    }
}
