using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private ListBox faultList;
        private TextBox faultDetails;
        private Label faultPath;
        private Button openFaultDirectoryButton;
        private CheckBox startupBox;
        private bool startupSyncing;
        private readonly Dictionary<string, DateTime> recentFaults = new Dictionary<string, DateTime>();
        private readonly List<FaultEntry> visibleFaults = new List<FaultEntry>();

        private Control BuildGeneralPage()
        {
            Panel page = new Panel { BackColor = CanvasColor, ForeColor = InkColor, Padding = new Padding(14),
                AutoScroll = true, AutoScrollMinSize = Size.Empty,
                HorizontalScroll = { Enabled = false } };
            BufferedTableLayoutPanel grid = Grid(1);
            settingsGeneralGrid = grid;
            grid.Dock = DockStyle.Top;
            grid.BackColor = CanvasColor;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowCount = 5;
            grid.RowStyles.Clear();
            for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            startupBox = new CheckBox { Text = "登录 Windows 后自动启动", Dock = DockStyle.Fill,
                ForeColor = InkColor, AccessibleName = "开机自启", AutoEllipsis = true };
            startupBox.CheckedChanged += async delegate
            {
                if (startupSyncing || OfflinePreview) return;
                bool enabled = startupBox.Checked;
                startupBox.Enabled = false;
                try { await Task.Run(() => StartupService.SetEnabled(enabled)); }
                catch (Exception error)
                {
                    if (IsDisposed || startupBox.IsDisposed) return;
                    RecordFault("启动项", error.Message, error);
                    MessageBox.Show(settingsForm, "无法修改启动项：" + error.Message, "AirStereo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { await RefreshStartupAsync(); }
            };
            grid.Controls.Add(startupBox, 0, 0);
            grid.Controls.Add(new Label { Text = ".NET " + Environment.Version + " · " +
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
                Dock = DockStyle.Fill, ForeColor = MutedColor, AutoEllipsis = true }, 0, 1);
            grid.Controls.Add(BuildUpdatePanel(), 0, 2);
            updateStatus = new Label { Text = PackageEnvironment.IsPackaged ?
                "MSIX 版由 Microsoft Store 管理更新" : "更新来源：Flourishze/AirStereo 正式 Release", Dock = DockStyle.Fill,
                ForeColor = MutedColor, AutoEllipsis = true };
            grid.Controls.Add(updateStatus, 0, 3);
            grid.Controls.Add(BuildAutoConnectOptions(), 0, 4);
            page.Controls.Add(grid);
            return page;
        }

        private Control BuildFaultPage()
        {
            Panel page = new Panel { BackColor = CanvasColor, ForeColor = InkColor, Padding = new Padding(10),
                AutoScroll = true, AutoScrollMinSize = Size.Empty,
                HorizontalScroll = { Enabled = false } };
            BufferedTableLayoutPanel grid = Grid(1);
            settingsFaultGrid = grid;
            grid.BackColor = CanvasColor;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowCount = 4;
            grid.RowStyles.Clear();
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            faultPath = new Label { Dock = DockStyle.Fill, ForeColor = MutedColor, AutoEllipsis = true };
            faultList = new ListBox { Dock = DockStyle.Fill, BackColor = PanelColor,
                ForeColor = InkColor, BorderStyle = BorderStyle.None, IntegralHeight = false,
                HorizontalScrollbar = true, AccessibleName = "历史故障列表" };
            faultDetails = new TextBox { Dock = DockStyle.Fill, BackColor = LogBackColor,
                ForeColor = LogTextColor, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Margin = new Padding(0, 8, 0, 6) };
            DesktopTheme.ApplyScrollbars(faultList);
            DesktopTheme.ApplyScrollbars(faultDetails);
            faultList.SelectedIndexChanged += delegate
            {
                int index = faultList.SelectedIndex;
                if (index < 0 || index >= visibleFaults.Count) return;
                FaultEntry entry = visibleFaults[index];
                faultDetails.Text = entry.Time.ToString("yyyy-MM-dd HH:mm:ss zzz") + " · " + entry.Category +
                    Environment.NewLine + entry.Message + Environment.NewLine + entry.Context + Environment.NewLine + entry.Detail;
            };
            BufferedTableLayoutPanel actions = Grid(2);
            actions.BackColor = CanvasColor;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Button refresh = NewButton("刷新记录", 120, Glyph.Refresh);
            refresh.Dock = DockStyle.Fill;
            refresh.Click += delegate { RefreshFaults(); };
            openFaultDirectoryButton = NewButton("打开故障文件夹", 160, Glyph.Folder);
            openFaultDirectoryButton.Dock = DockStyle.Fill;
            openFaultDirectoryButton.Click += async delegate { await OpenFaultDirectoryAsync(); };
            actions.Controls.Add(refresh, 0, 0);
            actions.Controls.Add(openFaultDirectoryButton, 1, 0);
            grid.Controls.Add(faultPath, 0, 0);
            grid.Controls.Add(faultList, 0, 1);
            grid.Controls.Add(faultDetails, 0, 2);
            grid.Controls.Add(actions, 0, 3);
            page.Controls.Add(grid);
            return page;
        }

        private async void RefreshStartup()
        {
            await RefreshStartupAsync();
        }

        private async Task RefreshStartupAsync()
        {
            if (OfflinePreview || startupBox == null || startupBox.IsDisposed || startupSyncing) return;
            startupSyncing = true;
            startupBox.Enabled = false;
            try
            {
                bool enabled = await Task.Run(() => StartupService.Enabled);
                if (IsDisposed || startupBox.IsDisposed) return;
                startupBox.Checked = enabled;
                startupBox.Enabled = true;
                uiTips.SetToolTip(startupBox, PackageEnvironment.IsPackaged ?
                    "使用 Windows 启动任务；系统中关闭的启动项须在 Windows 设置里重新启用。" : "登录 Windows 后启动到托盘");
            }
            catch (Exception error)
            {
                if (!IsDisposed && !startupBox.IsDisposed)
                {
                    startupBox.Enabled = false;
                    uiTips.SetToolTip(startupBox, error.Message);
                    RecordFault("启动项", error.Message, error);
                }
            }
            finally { startupSyncing = false; }
        }

        private string DiagnosticContext()
        {
            StringBuilder context = new StringBuilder(SelectionSummary() + "\r\n状态=" + statusLabel.Text + "\r\n" +
                "延迟=" + LatencyProfile.Resolve(selectedMode, customLatencyMs) + "ms · 音量=" + volumeBar.Value +
                "% · 平衡=" + balancePreference + " · EQ=" + calibrationProfile.Enabled + " · DPI=" + DeviceDpi);
            foreach (ReceiverGroup group in SelectedGroups())
                foreach (Receiver member in group.Members)
                    context.Append("\r\n设备=").Append(member.Instance).Append(" · ID=").Append(member.Identity)
                        .Append(" · ").Append(member.Address).Append(':').Append(member.Port)
                        .Append(" · 型号=").Append(member.Model).Append(" · TSID=").Append(group.StereoPairId);
            return context.ToString();
        }

        private void RecordFault(string category, string message, Exception error = null)
        {
            if (OfflinePreview || IsDisposed || exiting) return;
            if (InvokeRequired) { Post(delegate { RecordFault(category, message, error); }); return; }
            string key = category + "\n" + message;
            DateTime now = DateTime.UtcNow;
            if (recentFaults.TryGetValue(key, out DateTime previous) && (now - previous).TotalSeconds < 30) return;
            if (recentFaults.Count > 200) recentFaults.Clear();
            recentFaults[key] = now;
            FaultStore.Default.Record(category, message, error?.ToString() ?? "", DiagnosticContext());
            if (faultList != null && settingsForm.Visible) RefreshFaults();
            if (!Visible && trayIcon != null)
                trayIcon.ShowBalloonTip(5000, "AirStereo · " + category, message, ToolTipIcon.Warning);
        }

        private void RefreshFaults()
        {
            if (faultList == null || OfflinePreview) return;
            visibleFaults.Clear();
            visibleFaults.AddRange(FaultStore.Default.Read());
            faultList.Items.Clear();
            foreach (FaultEntry entry in visibleFaults)
                faultList.Items.Add(entry.Time.ToString("MM-dd HH:mm:ss") + " · " + entry.Category + " · " + entry.Message);
            bool hasFaults = visibleFaults.Count > 0;
            faultDetails.Text = hasFaults ? "" : "暂无故障记录。发生连接或其他异常后，故障信息会显示在这里。";
            if (hasFaults) faultList.SelectedIndex = 0;
            RefreshFaultPath();
        }

        private void RefreshFaultPath()
        {
            faultPath.Text = (FaultStore.Default.IsFallback ? "目录不可写，已保存到：" : "保存到：") + FaultStore.Default.DirectoryPath;
            if (FaultStore.Default.StorageError.Length > 0) faultPath.Text = "记录保存失败：" + FaultStore.Default.StorageError;
            uiTips.SetToolTip(faultPath, faultPath.Text);
            if (openFaultDirectoryButton != null)
                uiTips.SetToolTip(openFaultDirectoryButton, "打开故障记录所在文件夹：" + FaultStore.Default.DirectoryPath);
        }

        private async Task OpenFaultDirectoryAsync()
        {
            if (OfflinePreview || !openFaultDirectoryButton.Enabled) return;
            openFaultDirectoryButton.Enabled = false;
            try
            {
                // Folder access and Explorer startup must not block the audio/settings UI.
                await Task.Run(delegate
                {
                    string directory = FaultStore.Default.EnsureDirectory();
                    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(new ProcessStartInfo(explorer, "\"" + directory + "\"")
                    {
                        UseShellExecute = false
                    })?.Dispose();
                });
                if (!IsDisposed && !faultPath.IsDisposed) RefreshFaultPath();
            }
            catch (Exception error)
            {
                if (IsDisposed || faultDetails.IsDisposed) return;
                // No modal save/error dialogs: keep settings responsive and show the path inline.
                faultDetails.Text = "无法打开故障文件夹：" + error.Message + Environment.NewLine +
                    "请手动打开：" + FaultStore.Default.DirectoryPath;
            }
            finally
            {
                if (!IsDisposed && !openFaultDirectoryButton.IsDisposed)
                    openFaultDirectoryButton.Enabled = true;
            }
        }
    }
}

