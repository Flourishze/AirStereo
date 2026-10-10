using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    // All volume requests use an injected fake. No network, capture, settings files or real receivers.
    internal static class VolumeRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Private | BindingFlags.Public).GetValue(owner);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private).SetValue(owner, value);
        private static object Call(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, Private).Invoke(form, args);
        private static CheckBox Select(MainForm form, int index) => (CheckBox)Field(((IList)Field(form, "deviceRows"))[index], "Check");
        private static Receiver Speaker(int id, string gid = null) => new Receiver
        {
            Instance = "音量测试 " + id, Address = "192.0.2." + id, Port = 7000,
            Txt = TxtRecord.Parse("deviceid=00:11:22:33:44:" + id.ToString("X2") + (gid == null ? "" : " gid=" + gid))
        };
        private static void Populate(MainForm form, params Receiver[] receivers)
        {
            var groups = (List<ReceiverGroup>)Field(form, "groups");
            groups.Clear(); groups.AddRange(ReceiverCatalog.Group(new List<Receiver>(receivers)));
            Call(form, "RebuildDeviceList");
        }
        private static bool Wait(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            do { Application.DoEvents(); if (condition()) return true; Thread.Sleep(5); }
            while (clock.ElapsedMilliseconds < 4000);
            return false;
        }
        internal static void Verify(Action<string, bool, string> check)
        {
            using (var form = new MainForm { OfflinePreview = true, Opacity = 0 })
            using (var release = new ManualResetEventSlim(false))
            {
                form.Show();
                Populate(form, Speaker(1), Speaker(2));
                var slider = (ValueSlider)Field(form, "volumeBar");
                var mode = (CheckBox)Field(form, "realtimeVolumeBox");
                var button = (Button)Field(form, "volumeButton");
                var values = new List<int>();
                var counts = new List<int>();
                int active = 0, maxActive = 0;
                bool blockNext = false, failNext = false;
                form.VolumeApplyOverride = (group, percent, log) =>
                {
                    int concurrent = Interlocked.Increment(ref active);
                    lock (values)
                    {
                        maxActive = Math.Max(maxActive, concurrent);
                        values.Add(percent); counts.Add(group.Members.Count);
                    }
                    try
                    {
                        if (blockNext) { blockNext = false; if (!release.Wait(4000)) throw new TimeoutException("fake gate"); }
                        if (failNext) { failNext = false; throw new InvalidOperationException("synthetic failure"); }
                        return new List<string>();
                    }
                    finally { Interlocked.Decrement(ref active); }
                };
                Func<int> count = () => { lock (values) return values.Count; };
                Func<int> last = () => { lock (values) return values.Count == 0 ? -1 : values[values.Count - 1]; };
                Func<bool> idle = () => !(bool)Field(form, "volumeBusy") && Field(form, "pendingVolumeTarget") == null;
                check("volume mode defaults to manual and zero targets disables apply", !mode.Checked && !button.Enabled, null);
                Select(form, 0).Checked = true;
                slider.Value = 20;
                Call(form, "FlushPendingVolume");
                check("volume manual drag only changes display without sending", count() == 0 && button.Enabled &&
                    ((Label)Field(form, "volumeValue")).Text == "20%", null);
                button.PerformClick();
                bool done = Wait(() => count() == 1 && idle());
                check("volume manual check applies the selected value once", done && last() == 20, null);
                check("volume manual setting is serialized as off", (string)Call(form, "VolumeModeSettingsLine") == "realtimeVolume=0", null);

                mode.Checked = true;
                check("volume enabling realtime does not send until next adjustment", count() == 1 && !button.Enabled && slider.Enabled, null);
                check("volume realtime setting is serialized as on", (string)Call(form, "VolumeModeSettingsLine") == "realtimeVolume=1", null);
                blockNext = true;
                slider.Value = 21;
                Call(form, "FlushPendingVolume");
                bool started = Wait(() => count() == 2);
                slider.Value = 22; slider.Value = 23; slider.Value = 24;
                Call(form, "FlushPendingVolume");
                check("volume realtime serializes slow requests and retains latest drag", started && count() == 2 &&
                    (bool)Field(form, "volumeBusy") && slider.Enabled, null);
                release.Set();
                done = Wait(() => count() >= 3 && idle());
                check("volume realtime automatically flushes final value without check", done && count() == 3 && last() == 24 && maxActive == 1, null);
                slider.Value = 25; slider.Value = 26; slider.Value = 27;
                Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 4 && idle());
                check("volume fast drag coalesces intermediate values", done && last() == 27, null);

                slider.Value = 28;
                mode.Checked = false;
                Call(form, "FlushPendingVolume");
                check("volume disabling realtime cancels unsent change and restores check", count() == 4 && button.Enabled && idle(), null);
                slider.Value = 29;
                button.PerformClick();
                done = Wait(() => count() == 5 && idle());
                check("volume manual mode after realtime still requires check", done && last() == 29, null);

                mode.Checked = true;
                slider.Value = 30;
                Select(form, 0).Checked = false;
                Select(form, 1).Checked = true;
                Call(form, "FlushPendingVolume");
                check("volume selection change cancels stale request rather than retargeting", count() == 5 && idle(), null);
                Set(form, "featureSettingsSyncing", true);
                slider.Value = 31;
                Call(form, "LoadVolumeModeSetting", "1");
                Set(form, "featureSettingsSyncing", false);
                Call(form, "FlushPendingVolume");
                check("volume restoring settings never sends a volume command", count() == 5 && idle(), null);
                Set(form, "featureSettingsSyncing", true);
                Call(form, "LoadVolumeModeSetting", "invalid");
                Set(form, "featureSettingsSyncing", false);
                check("volume malformed preference safely falls back to manual", !mode.Checked && button.Enabled, null);

                mode.Checked = true;
                failNext = true;
                slider.Value = 32; Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 6 && idle());
                check("volume failure releases busy state without unbounded retry", done && count() == 6, null);
                slider.Value = 33; Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 7 && idle());
                check("volume adjustment recovers after a failed request", done && last() == 33, null);

                Set(form, "playing", true); Set(form, "streamReady", false);
                slider.Value = 34; Call(form, "FlushPendingVolume");
                check("volume realtime waits during playback connection", count() == 7 && Field(form, "pendingVolumeTarget") != null, null);
                Set(form, "streamReady", true); Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 8 && idle());
                check("volume realtime resumes when playback is ready", done && last() == 34, null);
                slider.Value = 35;
                Set(form, "playing", false); Call(form, "UpdateButtons"); Call(form, "FlushPendingVolume");
                check("volume stopping playback cancels pending session adjustment", count() == 8 && idle(), null);
                Set(form, "streamReady", false);
                Set(form, "scanning", true); slider.Value = 36; Call(form, "FlushPendingVolume");
                check("volume scanning does not create realtime requests", count() == 8 && idle(), null);
                Set(form, "scanning", false); Call(form, "UpdateButtons");

                Select(form, 0).Checked = true;
                slider.Value = 37; Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 9 && idle());
                check("volume independent stereo uses one request containing both targets", done && counts[counts.Count - 1] == 2 &&
                    ((PlaybackRoute)Call(form, "SelectedRoute")).SplitStereo, null);
                Call(form, "ClearSelection");
                Populate(form, Speaker(1, "native"), Speaker(2, "native"));
                Select(form, 0).Checked = true;
                slider.Value = 38; Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 10 && idle());
                check("volume native pair remains one logical full-stereo target", done && counts[counts.Count - 1] == 2 &&
                    ((PlaybackRoute)Call(form, "SelectedRoute")).NativePair, null);
                mode.Checked = false;
                slider.Value = 39;
                check("volume native pair manual drag does not auto-apply", count() == 10 && button.Enabled, null);
                button.PerformClick();
                done = Wait(() => count() == 11 && idle());
                check("volume native pair manual check applies both members", done && last() == 39 && counts[counts.Count - 1] == 2, null);
                mode.Checked = true;
                // Exercise keyboard and mouse-wheel via the slider's real event handlers.
                typeof(ValueSlider).GetMethod("OnKeyDown", Private).Invoke(slider, new object[] { new KeyEventArgs(Keys.Right) });
                typeof(ValueSlider).GetMethod("OnMouseWheel", Private).Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120) });
                Call(form, "FlushPendingVolume");
                done = Wait(() => count() == 12 && idle());
                check("volume keyboard and wheel use the same realtime apply path", done && last() == 49 && maxActive == 1, null);
                string previewDirectory = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(previewDirectory))
                {
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
                        bitmap.Save(System.IO.Path.Combine(previewDirectory, "popup-realtime-volume.png"));
                    }
                }
                release.Reset(); blockNext = true;
                slider.Value = 50; Call(form, "FlushPendingVolume");
                started = Wait(() => count() == 13);
                slider.Value = 51; mode.Checked = false;
                release.Set();
                done = Wait(() => count() == 13 && idle());
                check("volume disabling realtime during send lets current command finish but drops pending", started && done && last() == 50 && button.Enabled, null);
                slider.Value = 52; button.PerformClick();
                done = Wait(() => count() == 14 && idle());
                check("volume manual apply works after disabling an in-flight realtime change", done && last() == 52, null);
                mode.Checked = true;
                bool intermediateSent = false;
                for (int percent = 60; percent <= 69; percent++)
                {
                    slider.Value = percent;
                    Thread.Sleep(30); Application.DoEvents();
                    if (percent < 69 && count() > 14) intermediateSent = true;
                }
                done = Wait(() => idle() && last() == 69);
                check("volume sustained drag updates before release and automatically applies final value", intermediateSent && done && maxActive == 1, null);
                int beforeDispose = count();
                slider.Value = 40;
                form.Dispose();
                check("volume disposing cancels pending timer", count() == beforeDispose, null);
            }
        }
    }
}
