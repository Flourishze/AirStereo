using System;
using System.Collections.Generic;

namespace AirStereo
{
    /// <summary>One logical target can contain more than one physical receiver.</summary>
    public sealed class PlaybackRoute
    {
        public ReceiverGroup Target { get; private set; }
        public bool SplitStereo { get; private set; }
        public bool NativePair { get; private set; }
        public bool SupportsBalance { get { return SplitStereo || NativePair; } }

        public int EffectiveBalance(int preference)
        {
            return SupportsBalance ? Math.Max(-100, Math.Min(100, preference)) : 0;
        }

        private PlaybackRoute(ReceiverGroup target, bool splitStereo, bool nativePair)
        {
            Target = target;
            SplitStereo = splitStereo;
            NativePair = nativePair;
        }

        public static bool Independent(ReceiverGroup group)
        {
            // Name hints alone do not restrict independent selection. Explicit native pair
            // membership does: a missing peer must never turn a pair member into a manual L/R target.
            return group != null && group.Members.Count == 1 && !group.IsStereoPair &&
                group.StereoPairId.Length == 0 && group.Members[0].StereoPairId.Length == 0;
        }

        public static PlaybackRoute Resolve(ReceiverGroup first, ReceiverGroup second, bool stereo)
        {
            if (first == null || first.Members.Count == 0) return null;
            if (first.StereoPairId.Length > 0 && !first.IsStereoPair) return null;
            // Keep all existing system groups available as one logical target.
            if (!stereo) return new PlaybackRoute(first, false, first.IsStereoPair);
            // A configured pair already assigns L/R in its own receiver group. Sending
            // isolated mono to each half would fight that mapping, not improve it.
            if (first.IsStereoPair) return new PlaybackRoute(first, false, true);
            if (!Independent(first) || !Independent(second) ||
                string.Equals(first.Members[0].Identity, second.Members[0].Identity,
                    StringComparison.OrdinalIgnoreCase)) return null;
            ReceiverGroup pair = new ReceiverGroup
            {
                Name = first.Name + " + " + second.Name,
                Members = new List<Receiver> { first.Members[0], second.Members[0] }
            };
            return new PlaybackRoute(pair, true, false);
        }

        /// <summary>Resolves the logical route represented by the checked list rows.</summary>
        public static PlaybackRoute Resolve(IList<ReceiverGroup> selected)
        {
            if (selected == null || selected.Count == 0 || selected.Count > 2) return null;
            if (selected.Count == 1)
            {
                ReceiverGroup only = selected[0];
                if (only == null || only.Members.Count == 0) return null;
                if (only.StereoPairId.Length > 0 && !only.IsStereoPair) return null;
                return new PlaybackRoute(only, false, only.IsStereoPair);
            }

            if (!Independent(selected[0]) || !Independent(selected[1])) return null;
            return Resolve(selected[0], selected[1], true);
        }
    }
}
