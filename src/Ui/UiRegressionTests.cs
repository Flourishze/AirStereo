using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AirStereo.Audio;

namespace AirStereo.Ui
{
    /// <summary>Offline UI events with synthetic receivers. Never scans or opens an audio session.</summary>
    internal static class UiRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object owner, string name)
        {
            return owner.GetType().GetField(name, Private | BindingFlags.Public).GetValue(owner);
        }
        private static void Set(object owner, string name, object value)
        {
            owner.GetType().GetField(name, Private).SetValue(owner, value);
        }
        private static object Call(MainForm form, string method)
        {
            return typeof(MainForm).GetMethod(method, Private).Invoke(form, null);
        }
        private static CheckBox CheckAt(MainForm form, int index)
        {
            return (CheckBox)Field(((IList)Field(form, "deviceRows"))[index], "Check");
        }
        private static string RoleAt(MainForm form, int index)
        {
            return ((Label)Field(((IList)Field(form, "deviceRows"))[index], "Role")).Text;
        }
        private static Receiver Speaker(string name, int id, string gid = null)
        {
            return new Receiver { Instance = name, Address = "192.0.2." + id, Port = 7000,
                Txt = TxtRecord.Parse("deviceid=00:11:22:33:44:" + id.ToString("X2") +
                    (gid == null ? "" : " gid=" + gid)) };
        }
        private static void Populate(MainForm form, List<Receiver> receivers)
        {
            List<ReceiverGroup> groups = (List<ReceiverGroup>)Field(form, "groups");
            groups.Clear();
            groups.AddRange(ReceiverCatalog.Group(receivers));
            Call(form, "RebuildDeviceList");
        }

        public static void Run(Action<string, bool, string> check)
        {
            Exception failure = null;
            Thread thread = new Thread(() =>
            {
                try { FeatureRegressionTests.VerifyUi(check); Verify(check); }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) check("offline UI regression completed", false, failure.ToString());
        }

        private static void Verify(Action<string, bool, string> check)
        {
            // Exercise the first-launch path without showing a window, loading user
            // settings, scanning receivers, or starting audio. No prior tray action
            // should be required before a loss of focus schedules dismissal.
            using (MainForm popup = new MainForm())
            {
                popup.OfflinePreview = true;
                IntPtr handle = popup.Handle;
                // A hidden WinForms handle can still acquire focus in the isolated
                // test desktop. Disable it to model focus having moved elsewhere.
                popup.Enabled = false;
                System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)Field(popup, "dismissTimer");
                try
                {
                    popup.OfflinePreview = false;
                    typeof(MainForm).GetMethod("OnDeactivate", Private).Invoke(popup, new object[] { EventArgs.Empty });
                    bool dismissalScheduled = false;
                    // Snapshot immediately after the queued deactivate callback,
                    // before DoEvents can also process the 150 ms timer tick.
                    popup.BeginInvoke(new Action(() =>
                    {
                        dismissalScheduled = timer.Enabled;
                        timer.Stop();
                    }));
                    Application.DoEvents();
                    check("UI first launch schedules dismissal on focus loss without prior tray actions",
                        dismissalScheduled, "ContainsFocus=" + popup.ContainsFocus +
                        "; ActiveFormIsPopup=" + (Form.ActiveForm == popup));
                }
                finally
                {
                    timer.Stop();
                    popup.OfflinePreview = true;
                }
                Set(popup, "playing", true);
                MethodInfo codecUpdate = typeof(MainForm).GetMethod("UpdateCodecFromDiagnostics", Private);
                MethodInfo statusUpdate = typeof(MainForm).GetMethod("SetStatus", Private);
                statusUpdate.Invoke(popup, new object[] { "连接中…" });
                codecUpdate.Invoke(popup, new object[] { Session.ReceiverSession.CodecDiagnostics(true, true, 44100, 24) });
                statusUpdate.Invoke(popup, new object[] { "播放中" });
                check("UI connected tray status and tooltip show actual ALAC",
                    ((Label)Field(popup, "popupStatus")).Text == "播放中 · ALAC" &&
                    ((NotifyIcon)Field(popup, "trayIcon")).Text.Contains("当前编码 ALAC"), null);
                codecUpdate.Invoke(popup, new object[] { Session.ReceiverSession.CodecDiagnostics(false, false, 44100, 0) });
                check("UI tray reflects experimental PCM instead of requested default",
                    ((Label)Field(popup, "popupStatus")).Text == "播放中 · PCM" &&
                    ((NotifyIcon)Field(popup, "trayIcon")).Text.Contains("当前编码 PCM"), null);
                Set(popup, "playing", false);
                statusUpdate.Invoke(popup, new object[] { "已停止" });
                codecUpdate.Invoke(popup, new object[] { Session.ReceiverSession.CodecDiagnostics(true, true, 44100, 24) });
                check("UI stopped tray clears previous codec and ignores late diagnostics",
                    ((Label)Field(popup, "popupStatus")).Text == "已停止" &&
                    !((NotifyIcon)Field(popup, "trayIcon")).Text.Contains("当前编码"), null);
                Set(popup, "playing", true);
                statusUpdate.Invoke(popup, new object[] { "连接失败 · synthetic" });
                check("UI failed connection does not retain stale codec", Field(popup, "currentCodec") == null, null);
                Set(popup, "playing", false);
                Call(popup, "HideToTray");
                check("UI hiding cancels pending dismissal and retains the tray icon",
                    !timer.Enabled && ((NotifyIcon)Field(popup, "trayIcon")).Visible, null);
            }

            using (MainForm form = new MainForm())
            {
                Receiver a = Speaker("卧室", 1), b = Speaker("卧室 (2)", 2), c = Speaker("厨房", 3);
                Populate(form, new List<Receiver> { a, b, c });
                Button play = (Button)Field(form, "playButton");
                ValueSlider balance = (ValueSlider)Field(form, "stereoBalance");
                Button reset = (Button)Field(form, "resetBalanceButton");
                check("UI zero selected disables playback", !play.Enabled, null);
                CheckAt(form, 2).Checked = true;
                check("UI one of many plays full stereo with balance disabled", play.Enabled &&
                    !balance.Enabled && !reset.Enabled && !((PlaybackRoute)Call(form, "SelectedRoute")).SplitStereo, null);
                CheckAt(form, 2).Checked = false;
                CheckAt(form, 1).Checked = true;
                CheckAt(form, 0).Checked = true;
                check("UI suspected rows keep both checks in click order", CheckAt(form, 0).Checked &&
                    CheckAt(form, 1).Checked && RoleAt(form, 1) == "L" && RoleAt(form, 0) == "R", null);
                CheckAt(form, 2).Checked = true;
                check("UI third check is rejected with a visible limit message", !CheckAt(form, 2).Checked &&
                    ((Label)Field(form, "targetDetail")).Text.Contains("最多选择两只"), null);
                balance.Value = 65;
                CheckAt(form, 0).Checked = false;
                check("UI switching to single centers and disables balance", balance.Value == 0 && !balance.Enabled, null);
                CheckAt(form, 0).Checked = true;
                check("UI returning to stereo restores saved balance", balance.Value == 65 && balance.Enabled, null);

                a.Instance = "改名后的卧室"; a.Address = "192.0.2.101";
                b.Instance = "另一个名称"; b.Address = "192.0.2.102";
                Populate(form, new List<Receiver> { c, a, b });
                check("UI rescan restores checks and L/R by device ID despite rename/IP/order changes",
                    CheckAt(form, 1).Checked && CheckAt(form, 2).Checked &&
                    RoleAt(form, 2) == "L" && RoleAt(form, 1) == "R", null);

                LivePlaybackControl live = new LivePlaybackControl(true);
                Set(form, "playing", true); Set(form, "streamReady", true); Set(form, "livePlayback", live);
                Call(form, "UpdateButtons");
                balance.Value = -80;
                check("UI playing stereo permits live balance and L/R tests", live.Balance == -80 && balance.Enabled &&
                    ((Button)Field(form, "leftTestButton")).Enabled && ((Button)Field(form, "rightTestButton")).Enabled, null);
                check("UI locks receiver checks but retains row stop actions during playback",
                    !CheckAt(form, 0).Enabled && !CheckAt(form, 1).Enabled &&
                    ((Panel)Field(form, "targetList")).Enabled &&
                    ((Button)Field(((IList)Field(form, "deviceRows"))[1], "Action")).Enabled, null);
                Set(form, "playing", false); Set(form, "streamReady", false); Set(form, "livePlayback", null);
                Call(form, "ClearSelection");
                a.Txt.AddFrom(TxtRecord.Parse("gid=native")); b.Txt.AddFrom(TxtRecord.Parse("gid=native"));
                Populate(form, new List<Receiver> { a, b, c });
                CheckAt(form, 0).Checked = true;
                check("UI native pair is a single selectable row with balance enabled",
                    ((IList)Field(form, "deviceRows")).Count == 2 &&
                    ((PlaybackRoute)Call(form, "SelectedRoute")).NativePair && balance.Enabled, null);
                Populate(form, new List<Receiver> { a, c });
                check("UI incomplete native member is not labeled a confirmed pair",
                    !((PlaybackRoute)Call(form, "SelectedRoute")).NativePair && !balance.Enabled, null);

                a.Txt = TxtRecord.Parse("deviceid=00:11:22:33:44:01");
                b.Txt = TxtRecord.Parse("deviceid=00:11:22:33:44:02");
                Call(form, "ClearSelection");
                ((HashSet<string>)Field(form, "selectedKeys")).Add("group:00:11:22:33:44:01,00:11:22:33:44:02");
                ((List<string>)Field(form, "selectionOrder")).Add("group:00:11:22:33:44:01,00:11:22:33:44:02");
                Populate(form, new List<Receiver> { a, b, c });
                check("UI migrates old inferred group selection into two independent checks",
                    CheckAt(form, 0).Checked && CheckAt(form, 1).Checked &&
                    ((PlaybackRoute)Call(form, "SelectedRoute")).SplitStereo, null);
                a.Txt.AddFrom(TxtRecord.Parse("gid=native")); b.Txt.AddFrom(TxtRecord.Parse("gid=native"));
                Populate(form, new List<Receiver> { a, b, c });
                check("UI newly confirmed pair replaces both member selections once",
                    ((List<string>)Field(form, "selectionOrder")).Count == 1 &&
                    ((PlaybackRoute)Call(form, "SelectedRoute"))?.NativePair == true, null);
            }

            using (MainForm form = new MainForm())
            {
                const string pairId = "22222222-3333-5444-8555-666666666666";
                Receiver a = Speaker("卧室", 1, pairId + "+0");
                Receiver b = Speaker("卧室 (2)", 2, pairId + "+0+4E3DBF86-9427-44C3-B17D-F13346877D04");
                a.Txt.AddFrom(TxtRecord.Parse("tsid=" + pairId + " tsm=1 igl=0"));
                b.Txt.AddFrom(TxtRecord.Parse("tsid=" + pairId + " tsm=1 igl=1"));
                Populate(form, new List<Receiver> { a, b });
                CheckAt(form, 0).Checked = true;
                check("UI real HomePod suffixed-gid pair shows one native row, never manual L/R",
                    ((IList)Field(form, "deviceRows")).Count == 1 && RoleAt(form, 0) == "配对" &&
                    ((PlaybackRoute)Call(form, "SelectedRoute")).NativePair && ((Button)Field(form, "playButton")).Enabled, null);
                Populate(form, new List<Receiver> { a });
                check("UI missing confirmed native peer disables playback and explains why",
                    !((Button)Field(form, "playButton")).Enabled &&
                    ((ToolStripStatusLabel)Field(form, "statusLabel")).Text.Contains("信息不完整"), null);
                Populate(form, new List<Receiver> { b, a });
                check("UI rescanning the returning native peer restores one playable pair",
                    ((IList)Field(form, "deviceRows")).Count == 1 && CheckAt(form, 0).Checked &&
                    ((Button)Field(form, "playButton")).Enabled, null);
            }

            using (MainForm form = new MainForm())
            {
                Populate(form, new List<Receiver> { Speaker("卧室", 1), Speaker("书房", 2), Speaker("厨房", 3) });
                CheckAt(form, 0).Checked = true;
                CheckAt(form, 1).Checked = true;
                Form settings = (Form)Field(form, "settingsForm");
                check("UI popup is compact, borderless and always has a tray entry", form.Width == 410 &&
                    form.FormBorderStyle == FormBorderStyle.None && !form.ShowInTaskbar &&
                    ((NotifyIcon)Field(form, "trayIcon")).Visible, null);
                check("UI advanced controls live in settings, not the connection popup",
                    settings.Contains((Control)Field(form, "latencyBox")) &&
                    settings.Contains((Control)Field(form, "stereoBalance")) &&
                    settings.Contains((Control)Field(form, "calibrationButton")) &&
                    !form.Contains((Control)Field(form, "latencyBox")), null);
                ((ValueSlider)Field(form, "latencyBar")).Value = LatencyProfile.ToSlider(480);
                check("UI settings custom delay uses the existing latency controller",
                    (LatencyMode)Field(form, "selectedMode") == LatencyMode.Custom &&
                    Math.Abs((int)Field(form, "customLatencyMs") - 480) < 5 &&
                    ((Label)Field(form, "popupSummary")).Text.Contains("ms"), null);
                ((ValueSlider)Field(form, "stereoBalance")).Value = 40;
                typeof(Control).GetMethod("OnClick", Private).Invoke(Field(form, "resetBalanceButton"), new object[] { EventArgs.Empty });
                check("UI settings keeps stereo balance and center control enabled",
                    ((ValueSlider)Field(form, "stereoBalance")).Value == 0 && ((ValueSlider)Field(form, "stereoBalance")).Enabled &&
                    ((Button)Field(form, "resetBalanceButton")).Enabled, null);
                Rectangle work = new Rectangle(-1920, 0, 1920, 1040);
                Rectangle popup = MainForm.PopupBounds(work, new Size(410, 508), 8);
                check("UI popup anchors inside the notification monitor work area",
                    popup.Right == work.Right - 8 && popup.Bottom == work.Bottom - 8 && work.Contains(popup), null);
                check("UI popup clamps on small desktops", new Rectangle(0, 0, 320, 300).Contains(
                    MainForm.PopupBounds(new Rectangle(0, 0, 320, 300), new Size(410, 508), 8)), null);
                RenderPreview(form, "popup-stereo");
                RenderPreview(settings, "settings-stereo");
                Set(form, "playing", true); Set(form, "streamReady", true);
                Set(form, "livePlayback", new LivePlaybackControl(true));
                Call(form, "UpdateButtons");
                check("UI streaming keeps delay settings readable while locking changes", ((Control)Field(form, "latencyBox")).Enabled &&
                    ((ValueSlider)Field(form, "stereoBalance")).Enabled && ((Button)Field(form, "leftTestButton")).Enabled, null);
                check("UI streaming visually locks delay controls without disabling their text",
                    ((ValueSlider)Field(form, "latencyBar")).InputLocked &&
                    ((Array)Field(form, "latencyModes")).Length == 5 &&
                    ((Control)Field(form, "latencyBox")).Enabled, null);
                SettingsTabs playingTabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                playingTabs.SelectedIndex = 0;
                RenderPreview(settings, "settings-playing");
                RenderPreview(form, "popup-playing");
                form.OfflinePreview = true;
                form.Opacity = 0;
                form.Show();
                Call(form, "HideToTray");
                check("UI hiding the popup keeps the current playback session and tray alive", !form.Visible &&
                    (bool)Field(form, "playing") && ((NotifyIcon)Field(form, "trayIcon")).Visible, null);
                Call(form, "RestoreFromTray");
                check("UI tray reopens the popup without changing playback", form.Visible && (bool)Field(form, "playing"), null);
                FormClosingEventArgs closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                typeof(MainForm).GetMethod("OnFormClosing", Private).Invoke(form, new object[] { closing });
                check("UI closing the popup hides instead of terminating audio", closing.Cancel &&
                    !(bool)Field(form, "exiting") && (bool)Field(form, "playing"), null);
                Call(form, "OpenSettings");
                check("UI gear opens the settings window while playback continues", settings.Visible && (bool)Field(form, "playing"), null);
                SettingsTabs tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                ((Button)tabs.Navigation.Controls[1]).PerformClick();
                check("UI dark navigation switches pages without changing playback settings", tabs.SelectedIndex == 1 &&
                    (bool)Field(form, "playing") && ((ValueSlider)Field(form, "stereoBalance")).Value == 0, null);
                RenderPreview(settings, "settings-general");
                tabs.SelectedIndex = 2;
                ((Label)Field(form, "faultPath")).Text = "保存到：软件目录\\Diagnostics（离线界面示例）";
                ((TextBox)Field(form, "faultDetails")).Text = "离线预览：无真实设备故障记录";
                RenderPreview(settings, "settings-faults");
                ((DarkSettingsForm)settings).CloseWindowButton.PerformClick();
                check("UI dark settings close button hides without disposing or stopping audio", !settings.Visible &&
                    !settings.IsDisposed && (bool)Field(form, "playing"), null);
                Call(form, "OpenSettings");
                check("UI reopening settings preserves the selected page and controls", settings.Visible && tabs.SelectedIndex == 2, null);
                settings.Hide();
                Set(form, "playing", false); Set(form, "streamReady", false); Set(form, "livePlayback", null);
                typeof(MainForm).GetMethod("SetStatus", Private).Invoke(form, new object[] { "连接失败 · 模拟音响断开" });
                check("UI connection failure remains visible on the selected rows",
                    ((Label)Field(((IList)Field(form, "deviceRows"))[0], "State")).Text == "连接失败" &&
                    ((Label)Field(form, "popupStatus")).Text.Contains("模拟音响断开"), null);
            }

            VerifyPopupSelectionBounds(check);
            VerifyDiagnostics(check);

            using (MainForm form = new MainForm { OfflinePreview = true, StartInTray = true, Opacity = 0 })
            {
                form.Show();
                Application.DoEvents();
                check("UI startup tray entry never starts playback", !form.Visible &&
                    !(bool)Field(form, "playing") && ((NotifyIcon)Field(form, "trayIcon")).Visible, null);
                Call(form, "ExitFromTray");
                check("UI explicit tray exit terminates the popup", form.IsDisposed, null);
            }

            foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
            {
                using (MainForm form = new MainForm())
                {
                    Populate(form, new List<Receiver> { Speaker("测试音响 L", 1), Speaker("测试音响 R", 2) });
                    form.Scale(new SizeF(scale, scale));
                    form.PerformLayout();
                    string detail = "";
                    bool fits = Fits(form, ref detail);
                    check("UI simulated " + (int)(scale * 100) + "% scaling has no overlapping controls", fits, detail);
                    Form settings = (Form)Field(form, "settingsForm");
                    CreateHandles(settings);
                    settings.Scale(new SizeF(scale, scale));
                    // Scale() changes control bounds in this offline harness, but
                    // does not emulate Windows' per-monitor font scaling.  Apply
                    // the equivalent font size so the 175%/200% checks exercise
                    // the same layout path as a real high-DPI monitor.
                    settings.Font = new Font(settings.Font.FontFamily, 9F * scale, FontStyle.Regular);
                    settings.PerformLayout();
                    MethodInfo relayout = typeof(MainForm).GetMethod("ApplySettingsLayout", Private);
                    relayout.Invoke(form, new object[] { false });
                    int contentHeight = ((Control)Field(form, "settingsOptions")).Height;
                    int balanceHeight = ((Control)Field(form, "resetBalanceButton")).Height;
                    for (int repeat = 0; repeat < 12; repeat++)
                        relayout.Invoke(form, new object[] { false });
                    check("UI repeated settings layout does not grow rows at simulated " + (int)(scale * 100) + "%",
                        ((Control)Field(form, "settingsOptions")).Height == contentHeight &&
                        ((Control)Field(form, "resetBalanceButton")).Height == balanceHeight, null);
                    SettingsTabs tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                    for (int index = 0; index < tabs.PageCount; index++)
                    {
                        tabs.SelectedIndex = index;
                        settings.PerformLayout();
                        detail = "";
                        check("UI settings page " + index + " simulated " + (int)(scale * 100) + "% has no overlapping controls",
                            Fits(settings, ref detail), detail);
                        check("UI settings page " + index + " uses a consistent dark background at " + (int)(scale * 100) + "%",
                            HasDarkBackground(settings), null);
                        RenderPreview(settings, "settings-page-" + index + "-" + (int)(scale * 100));
                    }
                    tabs.SelectedIndex = 0;
                    RenderPreview(form, "popup-" + (int)(scale * 100));
                    RenderPreview(settings, "settings-" + (int)(scale * 100));
                    foreach (Size size in new[] { settings.MinimumSize, new Size((int)(900 * scale), (int)(750 * scale)) })
                    {
                        settings.Size = size;
                        settings.Opacity = 0;
                        settings.Show();
                        for (int index = 0; index < tabs.PageCount; index++)
                        {
                            tabs.SelectedIndex = index;
                            settings.PerformLayout();
                            detail = "";
                            check("UI settings page " + index + " fits " + size + " at simulated " + (int)(scale * 100) + "%",
                                Fits(settings, ref detail), detail);
                        }
                        tabs.SelectedIndex = 0;
                        foreach (int count in new[] { 1, 2 })
                        {
                            CheckAt(form, count - 1).Checked = true;
                            settings.PerformLayout();
                            detail = "";
                            check("UI settings " + count + " selected targets fit " + size + " at simulated " + (int)(scale * 100) + "%",
                                Fits(settings, ref detail), detail);
                        }
                        RenderPreview(settings, "settings-resized-" + size.Width + "-" + (int)(scale * 100));
                        CheckAt(form, 1).Checked = false;
                        CheckAt(form, 0).Checked = false;
                        settings.Hide();
                    }
                    form.ClientSize = new Size((int)(1100 * scale), (int)(900 * scale));
                    form.PerformLayout();
                    detail = "";
                    check("UI enlarged " + (int)(scale * 100) + "% layout has no overlapping controls", Fits(form, ref detail), detail);
                }
            }

            VerifyFontSizes(check);
        }

        private static void VerifyFontSizes(Action<string, bool, string> check)
        {
            // DPI scaling and the Windows text-size setting are independent.  Exercise
            // both: a larger message font must not turn the dark settings page into a
            // clipped or overlapping layout.
            foreach (float fontSize in new[] { 9F, 10F, 11F, 12F, 14F })
                foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
                    using (MainForm form = new MainForm { OfflinePreview = true, Opacity = 0 })
                    using (Font font = new Font(form.Font.FontFamily, fontSize, FontStyle.Regular))
                    {
                        form.Font = font;
                        Form settings = (Form)Field(form, "settingsForm");
                        settings.Font = font;
                        settings.Scale(new SizeF(scale, scale));
                        settings.Font = new Font(font.FontFamily, fontSize * scale, FontStyle.Regular);
                        settings.ClientSize = new Size((int)(760 * scale), (int)(620 * scale));
                        CreateHandles(settings);
                        SettingsTabs tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                        string detail = "";
                        for (int index = 0; index < tabs.PageCount; index++)
                        {
                            tabs.SelectedIndex = index;
                            settings.PerformLayout();
                            detail = "";
                            check("UI font " + fontSize.ToString("0") + "pt at " + (int)(scale * 100) +
                                "% settings page " + index + " fits", Fits(settings, ref detail), detail);
                        }

                        // Keep a representative high-font preview for visual inspection;
                        // the remaining combinations are covered by geometry checks.
                        if (fontSize == 14F && (scale == 1F || scale == 1.5F))
                        {
                            tabs.SelectedIndex = 0;
                            RenderPreview(settings, "settings-font-14-" + (int)(scale * 100));

                            Set(form, "playing", true);
                            Set(form, "streamReady", true);
                            Set(form, "livePlayback", new LivePlaybackControl(true));
                            Call(form, "UpdateButtons");
                            check("UI font 14pt at " + (int)(scale * 100) +
                                "% keeps locked delay controls readable while playing",
                                ((ValueSlider)Field(form, "latencyBar")).InputLocked &&
                                ((Control)Field(form, "latencyBox")).Enabled, null);
                            RenderPreview(settings, "settings-font-14-playing-" + (int)(scale * 100));
                        }
                    }
        }

        private static void VerifyPopupSelectionBounds(Action<string, bool, string> check)
        {
            foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
                foreach (int count in new[] { 1, 2, 5 })
                    using (MainForm form = new MainForm { OfflinePreview = true, Opacity = 0 })
                    {
                        List<Receiver> receivers = new List<Receiver>();
                        for (int i = 0; i < count; i++) receivers.Add(Speaker("选择尺寸测试 " + (i + 1), i + 1));
                        Populate(form, receivers);
                        form.Scale(new SizeF(scale, scale));
                        form.Show();
                        form.PerformLayout();
                        if (scale == 1F && count <= 2)
                        {
                            RenderPreview(form, "popup-before-check-" + count);
                            form.Show();
                        }
                        Rectangle bounds = form.Bounds;
                        Panel list = (Panel)Field(form, "targetList");
                        Size viewport = list.ClientSize;
                        string scenario = count + " rows at simulated " + (int)(scale * 100) + "%";
                        CheckAt(form, 0).Checked = true;
                        form.PerformLayout();
                        check("UI first check preserves popup bounds and list viewport with " + scenario,
                            form.Bounds == bounds && list.ClientSize == viewport,
                            "before=" + bounds + ", after=" + form.Bounds);
                        if (scale == 1F && count <= 2)
                        {
                            RenderPreview(form, "popup-after-check-" + count);
                            form.Show();
                        }
                        if (count > 1)
                        {
                            CheckAt(form, 1).Checked = true;
                            form.PerformLayout();
                            check("UI stereo second check preserves popup bounds with " + scenario,
                                form.Bounds == bounds && list.ClientSize == viewport && RoleAt(form, 0) == "L" && RoleAt(form, 1) == "R", null);
                            if (count > 2)
                            {
                                CheckAt(form, 2).Checked = true;
                                check("UI rejected third check does not move or shrink popup with " + scenario,
                                    !CheckAt(form, 2).Checked && form.Bounds == bounds && list.ClientSize == viewport, null);
                            }
                            CheckAt(form, 1).Checked = false;
                            check("UI stereo to single preserves popup bounds with " + scenario,
                                form.Bounds == bounds && list.ClientSize == viewport, null);
                        }
                        CheckAt(form, 0).Checked = false;
                        check("UI clearing checks preserves popup bounds with " + scenario,
                            form.Bounds == bounds && list.ClientSize == viewport && !((Button)Field(form, "playButton")).Enabled, null);
                    }
            using (MainForm form = new MainForm { OfflinePreview = true, Opacity = 0 })
            {
                string pair = "a63b1cde-2527-4b04-93b9-dc8d03c00554";
                Receiver left = Speaker("配对音响 L", 1), right = Speaker("配对音响 R", 2);
                left.Txt.AddFrom(TxtRecord.Parse("tsid=" + pair + " tsm=1 igl=0"));
                right.Txt.AddFrom(TxtRecord.Parse("tsid=" + pair + " tsm=1 igl=1"));
                Populate(form, new List<Receiver> { left, right, Speaker("独立音响", 3) });
                form.Show();
                Rectangle bounds = form.Bounds;
                CheckAt(form, 1).Checked = true;
                CheckAt(form, 0).Checked = true;
                check("UI selecting native pair replaces independent check without resizing popup",
                    form.Bounds == bounds && CheckAt(form, 0).Checked && !CheckAt(form, 1).Checked &&
                    ((PlaybackRoute)Call(form, "SelectedRoute"))?.NativePair == true, null);
            }
        }
        private static void VerifyDiagnostics(Action<string, bool, string> check)
        {
            using (CalibrationForm eq = new CalibrationForm(new AudioProfileController(), AudioProfile.Flat, null))
            {
                CreateHandles(eq);
                check("UI EQ uses the same dark title and surfaces as settings", HasDarkBackground(eq) &&
                    eq.FormBorderStyle == FormBorderStyle.None, null);
                RenderPreview(eq, "settings-eq");
            }
            using (MainForm emptyForm = new MainForm())
            {
                Call(emptyForm, "RefreshFaults");
                Button folderButton = (Button)Field(emptyForm, "openFaultDirectoryButton");
                TextBox details = (TextBox)Field(emptyForm, "faultDetails");
                check("UI can open the fault folder even when there are no records", folderButton.Enabled && folderButton.Text == "打开故障文件夹" &&
                    details.Text.Contains("暂无故障记录"), null);
            }
            string root = Path.Combine(AppContext.BaseDirectory, "selftest-artifacts", Guid.NewGuid().ToString("N"));
            string primary = Path.Combine(root, "Diagnostics");
            string fallback = Path.Combine(root, "Fallback");
            FaultStore store = new FaultStore(primary, fallback);
            store.Activity("synthetic activity: no receiver connected");
            store.Record("测试故障", "synthetic SETUP failure", "offline exception", "2 simulated targets");
            check("diagnostics saves faults in the application diagnostics directory",
                File.Exists(Path.Combine(primary, "faults.jsonl")) && !store.IsFallback, null);
            FaultStore reopened = new FaultStore(primary, fallback);
            List<FaultEntry> restored = reopened.Read();
            check("diagnostics persists exception and activity across restart", restored.Count == 1 &&
                restored[0].Detail == "offline exception" && restored[0].RecentActivity.Length == 1, null);
            string exported = Path.Combine(root, "report.json");
            reopened.Export(exported, "synthetic report");
            using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(exported)))
                check("diagnostics exports parseable faults and environment without protocol secrets",
                    report.RootElement.GetProperty("Faults").GetArrayLength() == 1 &&
                    report.RootElement.GetProperty("Runtime").GetString().Contains(".NET"), null);
            FaultStore emptyStore = new FaultStore(Path.Combine(root, "EmptyDiagnostics"), fallback);
            string emptyExport = Path.Combine(root, "empty-report.json");
            emptyStore.Export(emptyExport, "no recorded faults");
            using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(emptyExport)))
                check("diagnostics exports an empty report without blocking",
                    report.RootElement.GetProperty("Faults").GetArrayLength() == 0, null);
            check("update versions compare numerically rather than lexically", UpdateService.StableVersion("v1.10.0") > UpdateService.StableVersion("1.9.9"), null);
            Func<string, string> releaseJson = tag => "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/Flourishze/AirStereo/releases/tag/" + tag + "\"}";
            check("update same version is not an available update", !UpdateService.Parse(releaseJson("v" + VersionInfo.Current)).Available, null);
            check("update older release never offers a downgrade", !UpdateService.Parse(releaseJson("v1.0.0")).Available, null);
            Version currentUpdateVersion = UpdateService.StableVersion(VersionInfo.Current);
            string nextUpdateTag = "v" + new Version(currentUpdateVersion.Major, currentUpdateVersion.Minor, currentUpdateVersion.Build + 1).ToString(3);
            check("update higher version offers the designated release page", UpdateService.Parse(releaseJson(nextUpdateTag)).Available, null);
            foreach (string invalid in new[] { releaseJson(nextUpdateTag).Replace("\"draft\":false", "\"draft\":true"),
                releaseJson(nextUpdateTag).Replace("\"prerelease\":false", "\"prerelease\":true"),
                releaseJson(nextUpdateTag).Replace("Flourishze/AirStereo", "other/repository"), releaseJson(nextUpdateTag + "-rc1") })
            {
                bool rejected = false;
                try { UpdateService.Parse(invalid); } catch (FormatException) { rejected = true; }
                check("update rejects draft, prerelease, foreign link or malformed version", rejected, null);
            }
            using (UpdateHttpStub http = new UpdateHttpStub(HttpStatusCode.NotFound, "{}"))
                check("update missing release is explicit, never latest-version success", UpdateService.CheckAsync(http).GetAwaiter().GetResult().NoRelease, null);
            using (UpdateHttpStub http = new UpdateHttpStub(HttpStatusCode.OK, releaseJson(nextUpdateTag)))
            {
                UpdateResult result = UpdateService.CheckAsync(http).GetAwaiter().GetResult();
                check("update requests only designated repository with user agent and no credentials", result.Available &&
                    http.RequestUri == "https://api.github.com/repos/Flourishze/AirStereo/releases/latest" && http.HasUserAgent && !http.HasAuthorization, null);
            }
            using (UpdateHttpStub http = new UpdateHttpStub(HttpStatusCode.Forbidden, "{}"))
            {
                bool failed = false;
                try { UpdateService.CheckAsync(http).GetAwaiter().GetResult(); } catch (HttpRequestException) { failed = true; }
                check("update rate limits surface as failure, not already current", failed, null);
            }
            using (UpdateHttpStub http = new UpdateHttpStub(HttpStatusCode.OK, "{}", true))
            {
                bool timedOut = false;
                try { UpdateService.CheckAsync(http, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { timedOut = true; }
                check("update network wait is cancellable and timeout is bounded", timedOut, null);
            }
            string blocked = Path.Combine(root, "blocked-directory");
            File.WriteAllText(blocked, "not a directory");
            FaultStore denied = new FaultStore(blocked, fallback);
            denied.Record("测试回退", "synthetic write failure", "", "");
            check("diagnostics falls back when the install directory cannot be written", denied.IsFallback &&
                File.Exists(Path.Combine(fallback, "faults.jsonl")) && denied.StorageError.Length == 0, null);
            string command = StartupService.Command(root, @"C:\Program Files\dotnet\dotnet.exe");
            check("MSIX diagnostics avoids the immutable install directory",
                PackageEnvironment.DiagnosticDirectory(root, fallback, true) == Path.Combine(fallback, "Diagnostics"), null);
            check("unpackaged diagnostics retains the installation directory",
                PackageEnvironment.DiagnosticDirectory(root, fallback, false) == Path.Combine(root, "Diagnostics"), null);
            check("unpackaged app data path remains compatible with existing settings",
                PackageEnvironment.IsPackaged || PackageEnvironment.DataDirectory ==
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo"), null);
            check("startup command quotes paths and launches tray only", command.Contains("\" gui --tray") &&
                command.StartsWith("\"C:\\Program Files\\dotnet\\dotnet.exe\""), null);
            using (ValueSlider slider = new ValueSlider { Minimum = -100, Maximum = 100, Value = 0, SmallChange = 5 })
            {
                typeof(ValueSlider).GetMethod("OnKeyDown", Private).Invoke(slider, new object[] { new KeyEventArgs(Keys.Right) });
                check("UI slider supports keyboard adjustment and accessible value", slider.Value == 5 &&
                    slider.AccessibilityObject.Value == "5", null);
                slider.Value = 200;
                check("UI slider clamps out-of-range values", slider.Value == 100, null);
            }
        }

        private sealed class UpdateHttpStub : HttpMessageHandler
        {
            private readonly HttpStatusCode status;
            private readonly string content;
            private readonly bool wait;
            internal string RequestUri;
            internal bool HasUserAgent, HasAuthorization;
            internal UpdateHttpStub(HttpStatusCode status, string content, bool wait = false)
            { this.status = status; this.content = content; this.wait = wait; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                RequestUri = request.RequestUri.AbsoluteUri;
                HasUserAgent = request.Headers.UserAgent.Count > 0;
                HasAuthorization = request.Headers.Authorization != null;
                if (wait) await Task.Delay(Timeout.Infinite, cancellation).ConfigureAwait(false);
                return new HttpResponseMessage(status) { Content = new StringContent(content) };
            }
        }
        private static bool IsInactiveSettingsPage(Control control)
        {
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                if (parent is SettingsTabs tabs)
                {
                    for (int index = 0; index < tabs.PageCount; index++)
                        if (ReferenceEquals(control, tabs.PageAt(index))) return index != tabs.SelectedIndex;
                }
            return false;
        }

        private static bool HasDarkBackground(Control root)
        {
            if (root.BackColor.GetBrightness() > 0.35F) return false;
            foreach (Control child in root.Controls)
                if (!HasDarkBackground(child)) return false;
            return true;
        }

        private static void RenderPreview(Form form, string name)
        {
            string directory = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            MainForm main = form as MainForm;
            if (main != null) main.OfflinePreview = true;
            form.Opacity = 0;
            form.Show();
            form.PerformLayout();
            using (Bitmap image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(directory, name + ".png"));
            }
            form.Hide();
        }

        private static void CreateHandles(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) CreateHandles(child);
            control.PerformLayout();
        }

        private static bool Fits(Control parent, ref string detail)
        {
            // A hidden form reports Visible=false for all children, so inspect local geometry.
            foreach (Control child in parent.Controls)
            {
                if (IsInactiveSettingsPage(child) || (parent.Visible && !child.Visible)) continue;
                if (parent is not Form && child is not StatusStrip &&
                    !(parent is ScrollableControl scroll && scroll.AutoScroll) &&
                    (child.Left < 0 || child.Top < 0 || child.Right > parent.ClientSize.Width + 1 || child.Bottom > parent.ClientSize.Height + 1))
                {
                    detail = child.GetType().Name + " '" + child.Text + "' " + child.Bounds + " outside " + parent.ClientSize;
                    return false;
                }
                if (!Fits(child, ref detail)) return false;
            }
            for (int i = 0; i < parent.Controls.Count; i++)
                for (int j = i + 1; j < parent.Controls.Count; j++)
                {
                    Control a = parent.Controls[i], b = parent.Controls[j];
                    if (IsInactiveSettingsPage(a) || IsInactiveSettingsPage(b) || (parent.Visible && (!a.Visible || !b.Visible))) continue;
                    if (parent is TabControl && a is TabPage && b is TabPage) continue;
                    if (a.Bounds.IntersectsWith(b.Bounds))
                    {
                        detail = a.GetType().Name + " '" + a.Text + "' " + a.Bounds + " overlaps " +
                            b.GetType().Name + " '" + b.Text + "' " + b.Bounds;
                        return false;
                    }
                }
            return true;
        }
    }
}



