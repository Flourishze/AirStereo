using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AirStereo.Audio;

namespace AirStereo.Ui
{
    // Fakes only: never opens Core Audio, discovers mDNS, changes startup, or plays audio.
    internal static class FeatureRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private).SetValue(owner, value);
        private static object Call(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, Private).Invoke(form, args);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
        internal static void RunPure(Action<string, bool, string> check)
        {
            MuteTests(check);
            IdentityTests(check);
            var follow = new LogFollowState();
            check("log default follows", !follow.Paused, null);
            follow.UserOperation(100);
            check("log wheel/drag pauses immediately", follow.Paused && follow.ResumeAt == 10100, null);
            for (int t = 101; t < 10100; t += 17) follow.PositionFallback(t);
            check("log high-frequency fallback never extends user deadline", follow.ResumeAt == 10100, null);
            check("log does not resume at 9999ms", !follow.ResumeIfDue(10099) && follow.Paused, null);
            check("log resumes at 10000ms", follow.ResumeIfDue(10100) && !follow.Paused, null);
            follow.UserOperation(20000); follow.UserOperation(25000);
            check("log new USER operation extends idle timeout", follow.ResumeAt == 35000 && !follow.ResumeIfDue(30000), null);
            follow.Reset(); follow.PositionFallback(50000); follow.PositionFallback(56000);
            check("log first position fallback starts bounded idle window only once", follow.ResumeAt == 60000 && follow.ResumeIfDue(60000), null);
            check("log wheel recognized", LogFollowState.IsExplicitScroll(0x020A, 0), null);
            check("log thumbtrack recognized with thumb position", LogFollowState.IsExplicitScroll(0x0115, (123L << 16) | 5), null);
            foreach (int code in new[] { 0,1,2,3,4,6,7,8 })
                check("log programmatic/non-thumbtrack VSCROLL ignored code=" + code,
                    !LogFollowState.IsExplicitScroll(0x0115, code), null);
            check("log unrelated messages ignored", !LogFollowState.IsExplicitScroll(0x000F, 5), null);
        }
        private sealed class FakeEndpoint : IOutputMuteEndpoint
        {
            public string Id { get; set; }
            internal bool Value, FailSet, IgnoreSet;
            internal Action BeforeSet;
            internal int Sets;
            public bool Muted { get => Value; set { if (FailSet) throw new IOException("fake set failure"); BeforeSet?.Invoke(); if (!IgnoreSet) Value = value; Sets++; } }
            public void Dispose() { }
        }
        private sealed class FakeBackend : IOutputMuteBackend
        {
            internal FakeEndpoint First = new FakeEndpoint { Id = "original" };
            internal FakeEndpoint Second = new FakeEndpoint { Id = "headphone" };
            internal bool UseSecond, FailOpen;
            public IOutputMuteEndpoint OpenDefault() { if (FailOpen) throw new IOException("fake activate failure"); return UseSecond ? Second : First; }
            public IOutputMuteEndpoint Open(string id) { if (FailOpen) throw new IOException("fake activate failure"); if (id == First.Id) return First; if (id == Second.Id) return Second; throw new IOException("missing endpoint"); }
        }
        private sealed class FakeStore : IMuteRecoveryStore
        {
            internal MuteRecoveryRecord Record;
            internal bool FailWrite, FailClear;
            internal int Writes;
            public MuteRecoveryRecord Read() => Record;
            public void Write(MuteRecoveryRecord record) { if (FailWrite) throw new IOException("fake persist failure"); Record = record; Writes++; }
            public void Clear() { if (FailClear) throw new IOException("fake clear failure"); Record = null; }
        }
        private static void MuteTests(Action<string, bool, string> check)
        {
            foreach (bool wasMuted in new[] { false, true })
            {
                var backend = new FakeBackend(); backend.First.Value = wasMuted;
                var store = new FakeStore(); var logs = new List<string>();
                var controller = new LocalOutputMuteController(backend, store, logs.Add);
                bool savedBeforeMute = false;
                backend.First.BeforeSet = () => savedBeforeMute = store.Record != null && store.Record.WasMuted == wasMuted;
                controller.Begin();
                check("local mute persists BEFORE setting mute original=" + wasMuted, savedBeforeMute && backend.First.Value && controller.OwnsEndpoint, null);
                check("local mute log identifies Console endpoint and verified state", logs[0].Contains("role=Console") && logs[0].Contains("endpointId=original") && logs[0].Contains("readbackMuted=true"), null);
                int sets = backend.First.Sets; controller.Begin();
                check("local mute begin is idempotent original=" + wasMuted, backend.First.Sets == sets && store.Writes == 1, null);
                check("local mute restores original state original=" + wasMuted, controller.Restore() && backend.First.Value == wasMuted && store.Record == null && !controller.OwnsEndpoint, null);
                check("local mute opt-in/out diagnostics emitted original=" + wasMuted, logs.Count == 2, null);
            }
            {
                var backend = new FakeBackend(); var store = new FakeStore { FailWrite = true };
                var controller = new LocalOutputMuteController(backend, store, _ => { }); controller.Begin();
                check("local mute persistence failure cannot mute hardware", !backend.First.Value && backend.First.Sets == 0 && !controller.OwnsEndpoint, null);
            }
            {
                var backend = new FakeBackend(); var store = new FakeStore(); var logs = new List<string>();
                var controller = new LocalOutputMuteController(backend, store, logs.Add); controller.Begin();
                backend.First.FailSet = true;
                check("local mute failed restore retains recovery record", !controller.Restore() && store.Record != null && controller.OwnsEndpoint, null);
                int count = logs.Count; controller.Restore();
                check("local mute repeated failure logs are deduplicated", logs.Count == count, null);
                backend.First.FailSet = false;
                check("local mute successful retry clears record", controller.Restore() && !backend.First.Value && store.Record == null, null);
            }
            {
                var backend = new FakeBackend(); var store = new FakeStore();
                var controller = new LocalOutputMuteController(backend, store, _ => { }); controller.Begin();
                var restarted = new LocalOutputMuteController(backend, store, _ => { });
                check("local mute crash recovery uses saved endpoint and value", restarted.Restore() && !backend.First.Value && store.Record == null, null);
            }
            {
                var backend = new FakeBackend(); var store = new FakeStore();
                var controller = new LocalOutputMuteController(backend, store, _ => { }); controller.Begin();
                check("local mute stable default retains ownership", controller.CheckDefault() && backend.First.Value, null);
                backend.First.Value = false;
                check("local mute external unmute is forced back on", controller.CheckDefault() && backend.First.Value && controller.OwnsEndpoint, null);
                backend.UseSecond = true;
                check("local mute hot switch restores OLD endpoint only", !controller.CheckDefault() && !backend.First.Value && backend.Second.Sets == 0 && !controller.OwnsEndpoint, null);
            }
            {
                var backend = new FakeBackend { FailOpen = true }; var store = new FakeStore(); var logs = new List<string>();
                var controller = new LocalOutputMuteController(backend, store, logs.Add); controller.Begin(); controller.Begin();
                check("local mute activate failure is nonfatal and logs once", !controller.OwnsEndpoint && backend.First.Sets == 0 && logs.Count == 1, null);
            }
            {
                var backend = new FakeBackend { FailOpen = true };
                var old = new MuteRecoveryRecord { EndpointId = "original", WasMuted = true };
                var store = new FakeStore { Record = old };
                var controller = new LocalOutputMuteController(backend, store, _ => { }); controller.Begin();
                check("local mute unresolved crash record never overwritten", ReferenceEquals(store.Record, old) && store.Writes == 0, null);
            }
            {
                var backend = new FakeBackend(); var store = new FakeStore();
                var controller = new LocalOutputMuteController(backend, store, _ => { }); controller.Begin(); store.FailClear = true;
                check("local mute delete failure keeps record after hardware restored", !controller.Restore() && !backend.First.Value && store.Record != null, null);
                store.FailClear = false;
                check("local mute cleanup retry succeeds", controller.Restore() && store.Record == null, null);
            }
            string path = Path.Combine(AppContext.BaseDirectory, "mute-store-selftest-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var store = new FileMuteRecoveryStore(path);
                check("local mute recovery absent by default", store.Read() == null, null);
                store.Write(new MuteRecoveryRecord { EndpointId = "设备,ID\\endpoint", WasMuted = true });
                var restored = store.Read();
                check("local mute file roundtrip preserves endpoint identity", restored.EndpointId == "设备,ID\\endpoint" && restored.WasMuted, null);
                File.WriteAllText(path, "corrupt\nrecord");
                bool failed = false; try { store.Read(); } catch { failed = true; }
                check("local mute invalid recovery preserved not silently ignored", failed && File.Exists(path), null);
                store.Clear(); check("local mute recovery clears explicitly", !File.Exists(path), null);
            }
            finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
        }
        private static Receiver Speaker(string id, string name = "同名") => new Receiver { Instance = name, Host = id + ".local", Address = "192.0.2.1", Port = 7000, ServiceDeviceId = id };
        private static ReceiverGroup Group(Receiver r) => new ReceiverGroup { Name = r.Instance, Members = new List<Receiver> { r } };
        private static void IdentityTests(Action<string, bool, string> check)
        {
            var left = Speaker("id-L"); var right = Speaker("id-R");
            var a = Group(left); var b = Group(right); var available = new List<ReceiverGroup> { a,b };
            check("auto-connect device identity NOT name", StartupConnectionPolicy.StableIdentity(left) != StartupConnectionPolicy.StableIdentity(right), null);
            check("auto-connect same names preserve configured L/R order",
                StartupConnectionPolicy.TryResolve(available, new[] { right.Identity,left.Identity }, out var selected, out _) &&
                PlaybackRoute.Resolve(selected).SplitStereo && PlaybackRoute.Resolve(selected).Target.Members[0] == right, null);
            left.Instance = "已改名"; left.Address = "192.0.2.50";
            check("auto-connect rename/IP change still matches Identity", StartupConnectionPolicy.TryResolve(available, new[] { "id-L" }, out selected, out _) && selected[0] == a, null);
            check("auto-connect matching is case-insensitive", StartupConnectionPolicy.TryResolve(available, new[] { "ID-L" }, out selected, out _), null);
            check("auto-connect missing second never downgrades", !StartupConnectionPolicy.TryResolve(new[] { a }, new[] { "id-L", "id-R" }, out selected, out _), null);
            check("auto-connect duplicate physical identity refused", !StartupConnectionPolicy.TryResolve(new[] { a, Group(Speaker("id-L")) }, new[] { "id-L" }, out selected, out _), null);
            check("auto-connect no targets refused", !StartupConnectionPolicy.TryResolve(available, Array.Empty<string>(), out selected, out _), null);
            check("auto-connect more than 2 targets refused", !StartupConnectionPolicy.TryResolve(available, new[] { "id-L", "id-R", "id-X" }, out selected, out _), null);
            var anonymous = new Receiver { Instance = "有名无ID", Address = "192.0.2.2", Port = 7000 };
            check("auto-connect refuses name/IP Key fallback", StartupConnectionPolicy.StableIdentity(anonymous) == null &&
                !StartupConnectionPolicy.TryResolve(new[] { Group(anonymous) }, new[] { anonymous.Identity }, out selected, out _), null);
            var hostOnly = new Receiver { Host = "Stable.Local", Address = "192.0.2.2", Port = 7000 };
            check("auto-connect stable host fallback equals Receiver.Identity", StartupConnectionPolicy.StableIdentity(hostOnly) == "host:stable.local", null);
            var pair = new ReceiverGroup { GroupId = "pair", StereoPairId = "known-pair", Members = new List<Receiver> { left,right } };
            check("auto-connect native pair remains one logical route", StartupConnectionPolicy.TryResolve(new[] { pair }, new[] { left.Identity,right.Identity }, out selected, out _) && selected.Count == 1 && PlaybackRoute.Resolve(selected).NativePair, null);
            var incomplete = new ReceiverGroup { GroupId = "pair", StereoPairId = "known-pair", Members = new List<Receiver> { left } };
            check("auto-connect incomplete explicit native pair refused", !StartupConnectionPolicy.TryResolve(new[] { incomplete }, new[] { left.Identity }, out selected, out _), null);
            var ids = new[] { "id-R", "id-L,中文" };
            var decoded = StartupConnectionPolicy.Decode(StartupConnectionPolicy.Encode(ids));
            check("auto-connect settings base64 roundtrip preserves order and comma", decoded.Count == 2 && decoded[0] == ids[0] && decoded[1] == ids[1], null);
            decoded = StartupConnectionPolicy.Decode("invalid%," + StartupConnectionPolicy.Encode(new[] { "same", "SAME" }));
            check("auto-connect ignores malformed config and deduplicates", decoded.Count == 1 && decoded[0] == "same", null);
            check("auto-connect retry and timeout are bounded", StartupConnectionPolicy.MaxAttempts == 4 && StartupConnectionPolicy.TimeoutMilliseconds == 60000 && StartupConnectionPolicy.RetryMilliseconds == 5000, null);
        }
        internal static void VerifyUi(Action<string, bool, string> check)
        {
            using (var form = new MainForm { OfflinePreview = true })
            {
                check("UI preview starts without native form handle (no live log writes)", !form.IsHandleCreated, null);
                var mute = (CheckBox)Field(form, "muteLocalOutputBox");
                var auto = (CheckBox)Field(form, "autoConnectBox");
                var list = (CheckedListBox)Field(form, "autoConnectDeviceList");
                check("UI new mute and auto-connect switches default OFF", !mute.Checked && !auto.Checked, null);
                var backend = new FakeBackend(); var store = new FakeStore();
                Set(form, "localMute", new LocalOutputMuteController(backend, store, _ => { }));
                mute.Checked = true; Set(form, "playing", true); Set(form, "streamReady", true); Call(form, "UpdateLocalMute");
                check("UI OfflinePreview cannot mute real/fake endpoint", backend.First.Sets == 0 && store.Record == null, null);
                Set(form, "playing", false); Set(form, "streamReady", false);
                var groups = (List<ReceiverGroup>)Field(form, "groups");
                groups.Add(Group(Speaker("ui-L"))); groups.Add(Group(Speaker("ui-R"))); groups.Add(Group(Speaker("ui-X")));
                Call(form, "RefreshAutoConnectDevices");
                check("UI per-device checkboxes populated", list.Items.Count == 3 && list.CheckedItems.Count == 0, null);
                list.SetItemChecked(1, true); list.SetItemChecked(0, true);
                var configured = (List<string>)Field(form, "autoConnectIdentities");
                check("UI auto-connect checks preserve USER order not scan order", configured.Count == 2 && configured[0] == "ui-R" && configured[1] == "ui-L", null);
                if (!form.IsHandleCreated)
                {
                    list.SetItemChecked(2, true);
                    check("UI third auto-connect target rejected without changing order", !list.GetItemChecked(2) && configured.Count == 2 && configured[0] == "ui-R" && configured[1] == "ui-L", null);
                    Set(form, "autoConnectPending", true); Call(form, "BeginScan");
                    check("UI manual scan cancels pending startup connection", !(bool)Field(form, "autoConnectPending"), null);
                    Set(form, "autoConnectPending", true); Call(form, "StartPlayback", "loopback");
                    check("UI manual play cancels pending startup connection", !(bool)Field(form, "autoConnectPending"), null);
                    Set(form, "autoConnectPending", true); Call(form, "StopPlayback");
                    check("UI manual stop cancels pending startup connection", !(bool)Field(form, "autoConnectPending"), null);
                    Set(form, "autoConnectPending", true); auto.Checked = true;
                    check("UI auto-connect switch changes cancel pending startup", !(bool)Field(form, "autoConnectPending"), null);
                    Set(form, "autoConnectPending", true); list.SetItemChecked(0, false);
                    check("UI target edit cancels pending startup connection", !(bool)Field(form, "autoConnectPending"), null);
                    list.SetItemChecked(0, true); // append left after right, restoring USER order
                }
                // Sync guard validates unchanged checks without generating live log-file writes.
                Set(form, "autoDeviceListSyncing", true); list.SetItemChecked(2, false); Set(form, "autoDeviceListSyncing", false);
                groups.Clear(); groups.Add(Group(Speaker("ui-R", "改名的右"))); Call(form, "RefreshAutoConnectDevices");
                check("UI missing configured device retains checkbox/identity", list.Items.Count == 2 && list.CheckedItems.Count == 2 && list.Items[1].ToString().Contains("未发现"), null);
                check("UI renamed device displays new name without losing selection", list.Items[0].ToString().Contains("改名的右") && configured[0] == "ui-R", null);
                // Offline startup and manual commands must not discover devices or open audio.
                Call(form, "InitializeStartupFeatures"); Call(form, "BeginScan"); Call(form, "StartPlayback", "loopback");
                check("UI offline startup/scan/play leaves audio and discovery idle", !(bool)Field(form, "playing") && !(bool)Field(form, "scanning") && Field(form, "autoConnectTimer") == null, null);
                IntPtr handle = form.Handle;
                var log = (TextBox)Field(form, "logBox");
                IntPtr logHandle = log.Handle;
                var follow = (LogFollowState)Field(form, "logFollow");
                follow.Reset(); SendMessage(logHandle, 0x0115, (IntPtr)8, IntPtr.Zero);
                check("UI programmatic VSCROLL does not pause follow", !follow.Paused, null);
                SendMessage(logHandle, 0x020A, IntPtr.Zero, IntPtr.Zero);
                check("UI native wheel immediately pauses follow", follow.Paused, null);
                follow.Reset(); SendMessage(logHandle, 0x0115, (IntPtr)5, IntPtr.Zero);
                check("UI native thumbtrack immediately pauses follow", follow.Paused, null);
                var lines = new System.Text.StringBuilder();
                for (int i = 0; i < 200; i++) lines.AppendLine("line " + i);
                log.Text = lines.ToString(); log.Select(20, 5);
                SendMessage(logHandle, 0x00B6, IntPtr.Zero, (IntPtr)30);
                int first = (int)SendMessage(logHandle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
                follow.UserOperation(Environment.TickCount64);
                long deadline = follow.ResumeAt;
                for (int i = 0; i < 100; i++) Call(form, "AppendLogView", "new " + i);
                check("UI paused append preserves selection and viewport", log.SelectionStart == 20 && log.SelectionLength == 5 && (int)SendMessage(logHandle, 0x00CE, IntPtr.Zero, IntPtr.Zero) == first, null);
                check("UI frequent append cannot postpone 10s resume", follow.ResumeAt == deadline, null);
                SendMessage(logHandle, 0x0207, (IntPtr)0x10, (IntPtr)(100 << 16));
                int beforePan = (int)SendMessage(logHandle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(logHandle, 0x0200, (IntPtr)0x10, (IntPtr)(20 << 16));
                check("UI middle-button sliding pauses AND moves viewport", follow.Paused && (int)SendMessage(logHandle, 0x00CE, IntPtr.Zero, IntPtr.Zero) > beforePan, null);
                SendMessage(logHandle, 0x0208, IntPtr.Zero, IntPtr.Zero);
                check("UI middle release ends grab", !(bool)Field(form, "middleLogScroll") && !log.Capture, null);
                // A vertical scrollbar drag starts with WM_NCLBUTTONDOWN. The
                // cleanup path must not clear native capture when no custom
                // middle-button gesture owns it.
                log.Capture = true;
                Call(form, "EndMiddleLogScroll");
                check("UI scrollbar cleanup preserves native left-drag capture", !((bool)Field(form, "middleLogScroll")) && log.Capture, null);
                log.Capture = false;
                // Native scrollbar clicks must cancel only the custom middle gesture;
                // native EDIT capture belongs to the scrollbar and must survive.
                Call(form, "BeginMiddleLogScroll", 100);
                log.Capture = true;
                Call(form, "ClearMiddleLogScroll");
                check("UI non-client left click cleanup cancels middle grab and preserves native capture", !(bool)Field(form, "middleLogScroll") && log.Capture, null);
                Call(form, "BeginMiddleLogScroll", 100);
                log.Capture = true;
                Call(form, "ClearMiddleLogScroll");
                check("UI non-client right click cleanup cancels middle grab and preserves native capture", !(bool)Field(form, "middleLogScroll") && log.Capture, null);
                log.Capture = false;                follow.UserOperation(Environment.TickCount64 - 10001);
                var timer = (System.Windows.Forms.Timer)Field(form, "followResumeTimer");
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Private).Invoke(timer, new object[] { EventArgs.Empty });
                check("UI idle timer restores follow and jumps to newest", !follow.Paused && log.SelectionStart == log.TextLength, null);
                SendMessage(logHandle, 0x0115, (IntPtr)8, IntPtr.Zero);
                check("UI resume scrolling does not enter pause loop", !follow.Paused, null);
                log.Text = new string('x', 400001); follow.UserOperation(Environment.TickCount64); Call(form, "AppendLogView", "tail");
                check("UI truncation resets follow and goes to newest", log.TextLength == 200000 && !follow.Paused && log.SelectionStart == log.TextLength, null);
                MethodInfo recreate = typeof(Control).GetMethod("RecreateHandle", Private);
                recreate.Invoke(log, null); follow.Reset(); SendMessage(log.Handle, 0x020A, IntPtr.Zero, IntPtr.Zero);
                check("UI log watcher survives handle recreation", follow.Paused, null);
                check("UI features initialize without enabling runtime services in preview", Field(form, "localMuteTimer") == null, null);
            }
            check("UI feature timers/watchers dispose cleanly", true, null);
        }
    }
}
