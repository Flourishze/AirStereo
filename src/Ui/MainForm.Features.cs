using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AirStereo.Audio;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private CheckBox muteLocalOutputBox;
        private CheckBox autoConnectBox;
        private CheckedListBox autoConnectDeviceList;
        private Label autoConnectHint;
        private readonly List<string> autoConnectIdentities = new List<string>();
        private bool featureSettingsSyncing;
        private bool autoDeviceListSyncing;
        private bool autoConnectPending;
        private int autoConnectAttempts;
        private long autoConnectDeadline;
        private bool autoStartingPlayback;
        private System.Windows.Forms.Timer autoConnectTimer;
        private System.Windows.Forms.Timer localMuteTimer;
        private LocalOutputMuteController localMute;
        private bool muteAbandoned;

        private Control BuildMuteLocalOutput()
        {
            muteLocalOutputBox = new CheckBox { Text = "串流时静音本机输出", Dock = DockStyle.Fill,
                ForeColor = InkColor, AutoEllipsis = true, AccessibleName = "串流时静音本机输出（默认关闭）" };
            muteLocalOutputBox.CheckedChanged += delegate
            {
                if (featureSettingsSyncing) return;
                muteAbandoned = false;
                UpdateLocalMute();
                if (!OfflinePreview) SaveSettings();
            };
            return muteLocalOutputBox;
        }
        private Control BuildAutoConnectOptions()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = CanvasColor };
            autoConnectBox = new CheckBox { Text = "启动时自动连接下列音响", Dock = DockStyle.Top,
                Height = 38, ForeColor = InkColor, AutoEllipsis = true,
                AccessibleName = "启动时自动连接（默认关闭）" };
            autoConnectDeviceList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true,
                IntegralHeight = false, BackColor = PanelColor, ForeColor = InkColor,
                BorderStyle = BorderStyle.None, HorizontalScrollbar = true,
                AccessibleName = "自动连接设备（最多两只，勾选顺序为 L/R）" };
            autoConnectHint = new Label { Dock = DockStyle.Bottom, Height = 38, AutoEllipsis = true,
                ForeColor = MutedColor, Text = "最多两只；勾选顺序为 L/R。缺设备时等待，不自动降级。" };
            panel.Controls.Add(autoConnectDeviceList);
            panel.Controls.Add(autoConnectHint);
            panel.Controls.Add(autoConnectBox);
            autoConnectBox.CheckedChanged += delegate
            {
                if (featureSettingsSyncing) return;
                CancelAutoConnect("用户修改自动连接开关");
                if (!OfflinePreview) SaveSettings();
            };
            autoConnectDeviceList.ItemCheck += delegate(object sender, ItemCheckEventArgs args)
            {
                if (autoDeviceListSyncing) return;
                CancelAutoConnect("用户修改自动连接设备");
                var item = (AutoConnectDevice)autoConnectDeviceList.Items[args.Index];
                int existing = autoConnectIdentities.FindIndex(id => string.Equals(id, item.Identity, StringComparison.OrdinalIgnoreCase));
                if (args.NewValue == CheckState.Checked)
                {
                    if (existing < 0 && autoConnectIdentities.Count >= 2)
                    { args.NewValue = CheckState.Unchecked; Log("自动连接最多配置两只音响，请先取消其他勾选。"); return; }
                    if (existing < 0) autoConnectIdentities.Add(item.Identity);
                }
                else if (existing >= 0) autoConnectIdentities.RemoveAt(existing);
                if (!OfflinePreview) SaveSettings();
            };
            return panel;
        }
        private sealed class AutoConnectDevice
        {
            internal string Identity;
            internal string Name;
            public override string ToString() => Name + "  [" + Identity + "]";
        }
        private void RefreshAutoConnectDevices()
        {
            if (autoConnectDeviceList == null) return;
            autoDeviceListSyncing = true;
            autoConnectDeviceList.BeginUpdate();
            try
            {
                autoConnectDeviceList.Items.Clear();
                var available = new Dictionary<string, Receiver>(StringComparer.OrdinalIgnoreCase);
                foreach (ReceiverGroup group in groups)
                    foreach (Receiver receiver in group.Members)
                    {
                        string id = StartupConnectionPolicy.StableIdentity(receiver);
                        if (id != null) available[id] = receiver;
                    }
                // Preserve configured order and missing devices instead of losing their checks on a scan.
                foreach (string id in autoConnectIdentities)
                {
                    bool found = available.TryGetValue(id, out Receiver receiver);
                    autoConnectDeviceList.Items.Add(new AutoConnectDevice { Identity = id,
                        Name = found ? receiver.Instance : "未发现（保留配置）" }, true);
                    available.Remove(id);
                }
                foreach (var pair in available)
                    autoConnectDeviceList.Items.Add(new AutoConnectDevice { Identity = pair.Key, Name = pair.Value.Instance }, false);
            }
            finally { autoConnectDeviceList.EndUpdate(); autoDeviceListSyncing = false; }
        }
        private void InitializeStartupFeatures()
        {
            if (OfflinePreview) return;
            localMute = new LocalOutputMuteController(new WindowsOutputMuteBackend(),
                new FileMuteRecoveryStore(Path.Combine(Path.GetDirectoryName(SettingsPath()), "local-output-mute-recovery.txt")), Log);
            localMute.Restore(); // Recover even when the opt-in switch is currently off.
            localMuteTimer = new System.Windows.Forms.Timer { Interval = 500 };
            localMuteTimer.Tick += delegate { UpdateLocalMute(); };
            localMuteTimer.Start();
            autoConnectPending = autoConnectBox.Checked && autoConnectIdentities.Count > 0;
            autoConnectDeadline = Environment.TickCount64 + StartupConnectionPolicy.TimeoutMilliseconds;
            autoConnectTimer = new System.Windows.Forms.Timer { Interval = StartupConnectionPolicy.RetryMilliseconds };
            autoConnectTimer.Tick += delegate
            {
                autoConnectTimer.Stop();
                if (autoConnectPending && !scanning && !playing) BeginScanCore(true);
            };
            if (autoConnectPending) Log("启动自动连接：按设备身份等待目标，最多 " + StartupConnectionPolicy.MaxAttempts + " 次扫描，60 秒超时");
        }
        private void UpdateLocalMute()
        {
            if (OfflinePreview || localMute == null) return;
            if (!playing || !streamReady || !muteLocalOutputBox.Checked || exiting || muteAbandoned)
            { localMute.Restore(); return; }
            if (localMute.OwnsEndpoint && !localMute.CheckDefault()) muteAbandoned = true;
            if (!muteAbandoned) localMute.Begin();
        }
        private void CancelAutoConnect(string reason)
        {
            if (!autoConnectPending) return;
            autoConnectPending = false;
            autoConnectTimer?.Stop();
            Log("启动自动连接已取消：" + reason);
        }
        private void CompleteAutoConnectScan()
        {
            if (!autoConnectPending || OfflinePreview || exiting || playing) return;
            if (Environment.TickCount64 >= autoConnectDeadline)
            { RecordFault("启动自动连接", "60 秒等待窗口已结束，未降级连接；请手动扫描连接"); CancelAutoConnect("超时上限已到"); return; }
            if (StartupConnectionPolicy.TryResolve(groups, autoConnectIdentities, out List<ReceiverGroup> selected, out string reason))
            {
                autoConnectPending = false;
                autoConnectTimer?.Stop();
                ClearSelection();
                foreach (ReceiverGroup group in selected)
                { string key = SelectionKey(group); selectedKeys.Add(key); selectionOrder.Add(key); }
                RebuildDeviceList();
                Log("启动自动连接：目标完整，沿用 GUI 播放路由（按勾选顺序）");
                autoStartingPlayback = true;
                try { StartPlayback("loopback"); }
                finally { autoStartingPlayback = false; }
                return;
            }
            Log("启动自动连接 " + autoConnectAttempts + "/" + StartupConnectionPolicy.MaxAttempts + "：" + reason);
            if (autoConnectAttempts >= StartupConnectionPolicy.MaxAttempts || Environment.TickCount64 >= autoConnectDeadline)
            { RecordFault("启动自动连接", reason + "；重试/超时上限已到，未降级连接"); CancelAutoConnect("重试/超时上限已到，请手动扫描连接"); return; }
            autoConnectTimer.Start();
        }
        private void DisposeFeatures()
        {
            autoConnectPending = false;
            autoConnectTimer?.Dispose();
            localMuteTimer?.Dispose();
            if (!OfflinePreview) localMute?.Restore();
            followResumeTimer?.Dispose();
            logScrollWatcher?.Dispose();
        }

        private readonly LogFollowState logFollow = new LogFollowState();
        private System.Windows.Forms.Timer followResumeTimer;
        private LogScrollWatcher logScrollWatcher;
        private bool middleLogScroll;
        private int middleLogY;
        private int lastLogFirstLine;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
        private int LogFirstLine() => logBox.IsHandleCreated ? (int)SendMessage(logBox.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;
        private void SetupLogFollow()
        {
            logScrollWatcher = new LogScrollWatcher(logBox, PauseLogFollow, BeginMiddleLogScroll, MoveMiddleLogScroll, EndMiddleLogScroll, ClearMiddleLogScroll);
            followResumeTimer = new System.Windows.Forms.Timer { Interval = 200 };
            followResumeTimer.Tick += delegate
            {
                if (logBox.IsDisposed || !logBox.IsHandleCreated) return;
                int first = LogFirstLine();
                // Native middle-button auto-scroll can move without WM_MOUSEWHEEL.
                // Only actual viewport movement is another user operation, never a log append.
                if (middleLogScroll && first != lastLogFirstLine) PauseLogFollow();
                lastLogFirstLine = first;
                if (logFollow.ResumeIfDue(Environment.TickCount64))
                { EndMiddleLogScroll(); ScrollLogToEnd(); }
            };
            followResumeTimer.Start(); // periodic monotonic check also services first position-only fallback
        }
        private void BeginMiddleLogScroll(int y)
        {
            middleLogScroll = true;
            middleLogY = y;
        }
        private void MoveMiddleLogScroll(int y)
        {
            if (!middleLogScroll) return;
            PauseLogFollow();
            int lines = (middleLogY - y) / Math.Max(1, logBox.Font.Height);
            if (lines == 0) return;
            middleLogY -= lines * Math.Max(1, logBox.Font.Height);
            SendMessage(logBox.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines);
            lastLogFirstLine = LogFirstLine();
        }
        private void ClearMiddleLogScroll()
        {
            // A non-client click may be the native EDIT scrollbar starting a
            // left-button drag. Clear only our custom gesture state; never
            // release the control capture owned by the native scrollbar.
            middleLogScroll = false;
        }
        private void EndMiddleLogScroll()
        {
            // Do not clear native EDIT capture for an ordinary non-client click.
            // The vertical scrollbar starts its left-button drag from
            // WM_NCLBUTTONDOWN; clearing Capture here cancels that drag before
            // the EDIT control can process it. We only release capture if it
            // belonged to our middle-button gesture.
            bool releaseCapture = middleLogScroll && !logBox.IsDisposed && logBox.Capture;
            middleLogScroll = false;
            if (releaseCapture) logBox.Capture = false;
        }
        private void PauseLogFollow() => logFollow.UserOperation(Environment.TickCount64);
        private void ScrollLogToEnd()
        {
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.ScrollToCaret();
            lastLogFirstLine = LogFirstLine();
        }
        private static bool IsLastLineVisible(TextBox box)
        {
            if (box.TextLength == 0 || box.ClientSize.Height <= 0 || !box.Visible) return true;
            Point last = box.GetPositionFromCharIndex(box.TextLength - 1);
            return last.Y >= 0 && last.Y + box.Font.Height <= box.ClientSize.Height;
        }
        private void AppendLogView(string line)
        {
            if (!logFollow.Paused && !IsLastLineVisible(logBox)) logFollow.PositionFallback(Environment.TickCount64);
            int first = LogFirstLine();
            int start = logBox.SelectionStart, length = logBox.SelectionLength;
            logBox.AppendText(line + Environment.NewLine);
            if (logBox.TextLength > 400000)
            {
                logBox.Text = logBox.Text.Substring(logBox.TextLength - 200000);
                logBox.ClearUndo();
                logFollow.Reset();
                EndMiddleLogScroll();
            }
            if (!logFollow.Paused) ScrollLogToEnd();
            else
            {
                // AppendText itself changes the caret/viewport. Preserve both while paused.
                logBox.Select(start, length);
                if (logBox.IsHandleCreated)
                    SendMessage(logBox.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(first - LogFirstLine()));
                lastLogFirstLine = LogFirstLine();
            }
            logBox.Refresh();
        }
        private sealed class LogScrollWatcher : NativeWindow, IDisposable
        {
            private readonly Control target;
            private readonly Action scroll, cancelMiddle, clearMiddle;
            private readonly Action<int> middle, moveMiddle;
            internal LogScrollWatcher(Control target, Action scroll, Action<int> middle, Action<int> moveMiddle, Action cancelMiddle, Action clearMiddle)
            {
                this.target = target; this.scroll = scroll; this.middle = middle; this.moveMiddle = moveMiddle; this.cancelMiddle = cancelMiddle; this.clearMiddle = clearMiddle;
                target.HandleCreated += Created;
                target.HandleDestroyed += Destroyed;
                if (target.IsHandleCreated) AssignHandle(target.Handle);
            }
            private void Created(object sender, EventArgs args) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(target.Handle); }
            private void Destroyed(object sender, EventArgs args) { ReleaseHandle(); }
            protected override void WndProc(ref Message message)
            {
                if (LogFollowState.IsExplicitScroll(message.Msg, message.WParam.ToInt64())) scroll();
                int y = (short)((message.LParam.ToInt64() >> 16) & 0xffff);
                if (message.Msg == 0x0207) { middle(y); scroll(); } // middle-button grab/pan; EDIT has no guaranteed auto-scroll
                if (message.Msg == 0x0200 && (message.WParam.ToInt64() & 0x10) != 0) moveMiddle(y);
                if (message.Msg == 0x0208 || message.Msg == 0x0215) cancelMiddle(); // release / lost capture
                if (message.Msg == 0x0201 || message.Msg == 0x0204) cancelMiddle();
                if (message.Msg == 0x00A1 || message.Msg == 0x00A4) clearMiddle();
                if (message.Msg == 0x0100)
                {
                    Keys key = (Keys)message.WParam.ToInt32();
                    if (key == Keys.PageUp || key == Keys.PageDown || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End)
                    { cancelMiddle(); scroll(); }
                    if (key == Keys.Escape) cancelMiddle();
                }
                base.WndProc(ref message);
            }
            public void Dispose()
            { target.HandleCreated -= Created; target.HandleDestroyed -= Destroyed; ReleaseHandle(); }
        }
    }
}
