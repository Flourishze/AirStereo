using System;
using System.Collections.Generic;
using System.Text;

namespace AirStereo.Ui
{
    internal static class StartupConnectionPolicy
    {
        internal const int MaxAttempts = 4;
        internal const int RetryMilliseconds = 5000;
        internal const int TimeoutMilliseconds = 60000;
        internal static string StableIdentity(Receiver receiver)
        {
            // Receiver.Identity falls back to a display-name/IP Key when both are missing.
            // That fallback is suitable for a visible row, not for unattended connection.
            return receiver != null && (!string.IsNullOrWhiteSpace(receiver.DeviceId) ||
                !string.IsNullOrWhiteSpace(receiver.Host)) ? receiver.Identity : null;
        }
        internal static List<string> Decode(string value)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in (value ?? "").Split(','))
            {
                if (token.Length == 0) continue;
                try
                {
                    string id = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) result.Add(id);
                }
                catch (FormatException) { }
            }
            return result;
        }
        internal static string Encode(IList<string> ids)
        {
            var tokens = new List<string>();
            foreach (string id in ids) tokens.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(id)));
            return string.Join(",", tokens);
        }
        internal static bool TryResolve(IList<ReceiverGroup> available, IList<string> wanted,
            out List<ReceiverGroup> selected, out string reason)
        {
            selected = new List<ReceiverGroup>();
            if (wanted.Count == 0 || wanted.Count > 2)
            { reason = "请配置 1 或 2 个自动连接设备"; return false; }
            foreach (string id in wanted)
            {
                ReceiverGroup match = null;
                int hits = 0;
                foreach (ReceiverGroup group in available)
                    foreach (Receiver member in group.Members)
                        if (string.Equals(StableIdentity(member), id, StringComparison.OrdinalIgnoreCase))
                        { match = group; hits++; }
                if (hits != 1) { reason = hits == 0 ? "等待设备 identity=" + id : "设备身份重复，拒绝自动连接 identity=" + id; return false; }
                if (!selected.Contains(match)) selected.Add(match);
            }
            if (PlaybackRoute.Resolve(selected) == null)
            { reason = "目标路由不完整或不兼容（不降级单设备、不推断原生配对）"; return false; }
            reason = "目标设备完整";
            return true;
        }
    }
    internal sealed class LogFollowState
    {
        internal const int ResumeMilliseconds = 10000;
        internal bool Paused { get; private set; }
        internal long ResumeAt { get; private set; }
        internal void UserOperation(long now) { Paused = true; ResumeAt = now + ResumeMilliseconds; }
        // Appending logs is NOT an operation. Only the initial fallback pause gets a deadline.
        internal void PositionFallback(long now)
        { if (!Paused) { Paused = true; ResumeAt = now + ResumeMilliseconds; } }
        internal bool ResumeIfDue(long now)
        { if (!Paused || now < ResumeAt) return false; Reset(); return true; }
        internal void Reset() { Paused = false; ResumeAt = 0; }
        internal static bool IsExplicitScroll(int message, long wparam)
            => message == 0x020A || (message == 0x0115 && (wparam & 0xffff) == 5);
    }
}
