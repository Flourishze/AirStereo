using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private ToolTip uiTips;
        private Form settingsForm;
        private Label popupStatus;
        private Label popupSummary;
        private bool positioningPopup;
        private System.Windows.Forms.Timer dismissTimer;
        internal bool StartInTray { get; set; }

        private int UiPixels(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96.0));

        private void BuildCompactLayout()
        {
            uiTips = new ToolTip { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 12000 };
            dismissTimer = new System.Windows.Forms.Timer { Interval = 150 };
            dismissTimer.Tick += delegate
            {
                dismissTimer.Stop();
                if (!exiting && Visible && ActiveForm != this &&
                    !(settingsForm?.Visible == true) && !(calibrationForm?.Visible == true)) HideToTray();
            };
            BufferedTableLayoutPanel root = new BufferedTableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10),
                BackColor = CanvasColor, ColumnCount = 1, RowCount = 6
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 44F, 0F, 32F, 40F, 56F, 48F })
                root.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute,
                    height == 0 ? 100 : height));

            BufferedTableLayoutPanel header = Grid(4);
            header.BackColor = CanvasColor;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            Label brand = new Label { Text = "AirStereo", Font = SafeBold(Font, 15F),
                ForeColor = InkColor, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            header.Controls.Add(brand, 0, 0);
            scanButton = CompactIcon("刷新音响", Glyph.Refresh);
            scanButton.Click += delegate { BeginScan(); };
            Button settings = CompactIcon("音频设置", Glyph.Settings);
            settings.Click += delegate { OpenSettings(); };
            Button hide = CompactIcon("收起到托盘", Glyph.Hide);
            hide.Click += delegate { HideToTray(); };
            header.Controls.Add(scanButton, 1, 0);
            header.Controls.Add(settings, 2, 0);
            header.Controls.Add(hide, 3, 0);
            root.Controls.Add(header, 0, 0);

            targetList = new Panel { Dock = DockStyle.Fill, BackColor = CanvasColor,
                AutoScroll = true, Margin = new Padding(0, 6, 0, 0) };
            root.Controls.Add(targetList, 0, 1);
            targetDetail = new Label { Dock = DockStyle.Fill, ForeColor = MutedColor,
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            root.Controls.Add(targetDetail, 0, 2);

            BufferedTableLayoutPanel actions = Grid(2);
            actions.BackColor = CanvasColor;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            playButton = NewButton("播放电脑声音", 200, Glyph.Play);
            playButton.Dock = DockStyle.Fill;
            playButton.Margin = new Padding(0, 2, 6, 2);
            playButton.Click += delegate { StartPlayback("loopback"); };
            stopButton = CompactIcon("停止整个会话", Glyph.Stop);
            stopButton.Click += delegate { StopPlayback(); };
            actions.Controls.Add(playButton, 0, 0);
            actions.Controls.Add(stopButton, 1, 0);
            root.Controls.Add(actions, 0, 3);

            BufferedTableLayoutPanel volume = Grid(4);
            volume.Padding = new Padding(8, 8, 6, 8);
            volume.Margin = new Padding(0, 4, 0, 4);
            volume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            volume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            volume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            volume.Controls.Add(new Label { Text = "音量", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = InkColor }, 0, 0);
            volumeBar = new ValueSlider { Minimum = 0, Maximum = 100, Value = 65,
                SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill,
                Margin = new Padding(0), AccessibleName = "会话音量" };
            volumeValue = new Label { Text = "65%", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = InkColor, AutoEllipsis = true };
            volumeBar.ValueChanged += delegate { volumeValue.Text = volumeBar.Value + "%"; };
            volumeButton = CompactIcon("应用音量到当前所选音响", Glyph.Check);
            volumeButton.Click += delegate { ApplyVolume(); };
            volume.Controls.Add(volumeBar, 1, 0);
            volume.Controls.Add(volumeValue, 2, 0);
            volume.Controls.Add(volumeButton, 3, 0);
            root.Controls.Add(volume, 0, 4);

            BufferedTableLayoutPanel footer = Grid(1);
            footer.BackColor = CanvasColor;
            footer.RowCount = 2;
            footer.RowStyles.Clear();
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            popupStatus = new Label { Text = "正在准备…", ForeColor = AccentColor, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, AccessibleName = "连接状态" };
            popupSummary = new Label { ForeColor = MutedColor, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            footer.Controls.Add(popupStatus, 0, 0);
            footer.Controls.Add(popupSummary, 0, 1);
            root.Controls.Add(footer, 0, 5);
            Controls.Add(root);

            // Settings owns the existing controls even when hidden. Audio code keeps using
            // the same fields and event handlers; only their presentation has moved.
            statusLabel = new ToolStripStatusLabel("正在准备…");
            BuildSettingsWindow();
            UpdateRouteUi();
        }

        private static BufferedTableLayoutPanel Grid(int columns)
        {
            BufferedTableLayoutPanel grid = new BufferedTableLayoutPanel
            { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = 1 };
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return grid;
        }

        private Button CompactIcon(string name, Glyph glyph)
        {
            Button button = NewButton("", 32, glyph);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(2, 3, 2, 3);
            button.AccessibleName = name;
            uiTips.SetToolTip(button, name);
            return button;
        }

        private void BuildSettingsWindow()
        {
            settingsForm = new DarkSettingsForm { Text = "AirStereo · 音频设置", Font = Font,
                BackColor = CanvasColor, ForeColor = InkColor,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(560, 490), MinimumSize = new Size(540, 490),
                ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen };
            settingsForm.FormClosing += delegate (object sender, FormClosingEventArgs args)
            {
                if (!exiting && args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    settingsForm.Hide();
                    TopMost = true;
                    if (!OfflinePreview) SaveSettings();
                }
            };
            SettingsTabs tabs = new SettingsTabs();
            Panel audio = new Panel { BackColor = CanvasColor, Padding = new Padding(10) };
            Panel logs = new Panel { BackColor = CanvasColor, Padding = new Padding(10) };
            BufferedTableLayoutPanel options = Grid(1);
            options.BackColor = CanvasColor;
            options.RowCount = 4;
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            options.RowStyles.Clear();
            foreach (float height in new[] { 142F, 190F, 42F, 0F })
                options.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height));
            options.Controls.Add(BuildRouteBox(), 0, 0);
            options.Controls.Add(BuildSettingsLatency(), 0, 1);
            BufferedTableLayoutPanel commands = Grid(2);
            commands.BackColor = CanvasColor;
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            patternButton = NewButton("循环声道测试", 160, Glyph.Wave);
            patternButton.Dock = DockStyle.Fill;
            patternButton.Click += delegate { StartPlayback("pattern"); };
            calibrationButton = NewButton("均衡器与校准", 160, Glyph.Wave);
            calibrationButton.Dock = DockStyle.Fill;
            calibrationButton.Click += delegate { OpenCalibration(); };
            commands.Controls.Add(patternButton, 0, 0);
            commands.Controls.Add(calibrationButton, 1, 0);
            options.Controls.Add(commands, 0, 2);
            audio.Controls.Add(options);
            logs.Controls.Add(BuildLogBox());
            tabs.AddPage("音频", audio);
            tabs.AddPage("常规", BuildGeneralPage());
            tabs.AddPage("故障记录", BuildFaultPage());
            tabs.AddPage("诊断日志", logs);
            ((DarkSettingsForm)settingsForm).ContentHost.Controls.Add(tabs);
        }

        private Control BuildSettingsLatency()
        {
            latencyBox = new ThemedSection("延迟");
            latencyBox.Dock = DockStyle.Fill;
            latencyBox.Margin = new Padding(0, 0, 0, 8);
            latencyBox.Padding = new Padding(10, 26, 10, 8);
            BufferedTableLayoutPanel table = Grid(1);
            table.RowCount = 4;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Clear();
            foreach (float height in new[] { 62F, 32F, 25F, 25F })
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            BufferedTableLayoutPanel modes = Grid(3);
            modes.RowCount = 2;
            modes.RowStyles.Clear();
            modes.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            modes.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < 3; i++) modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            string[] names = { "实时 · 120 ms", "标准 · 200 ms", "缓冲 · 500 ms", "稳定 · 1000 ms", "自定义" };
            latencyModes = new RadioButton[5];
            for (int i = 0; i < latencyModes.Length; i++)
            {
                RadioButton mode = new RadioButton { Text = names[i], Tag = (LatencyMode)i,
                    Dock = DockStyle.Fill, ForeColor = InkColor, AutoEllipsis = true, Margin = new Padding(0) };
                mode.CheckedChanged += OnLatencyModeChanged;
                latencyModes[i] = mode;
                modes.Controls.Add(mode, i % 3, i / 3);
            }
            table.Controls.Add(modes, 0, 0);
            latencyBar = new ValueSlider { Minimum = 0, Maximum = LatencyProfile.SliderSteps,
                Dock = DockStyle.Fill, Margin = new Padding(0),
                SmallChange = 10, LargeChange = 50, AccessibleName = "自定义缓冲延迟" };
            latencyBar.ValueChanged += OnLatencySlider;
            table.Controls.Add(latencyBar, 0, 1);
            latencyValue = new Label { Dock = DockStyle.Fill, ForeColor = AccentDarkColor, AutoEllipsis = true };
            latencyHint = new Label { Dock = DockStyle.Fill, ForeColor = MutedColor, AutoEllipsis = true };
            table.Controls.Add(latencyValue, 0, 2);
            table.Controls.Add(latencyHint, 0, 3);
            latencyBox.Controls.Add(table);
            ApplyLatencySettings(selectedMode, customLatencyMs);
            return latencyBox;
        }

        private void OpenSettings()
        {
            if (exiting || IsDisposed) return;
            if (!Visible) RestoreFromTray();
            RefreshStartup();
            RefreshFaults();
            TopMost = false;
            if (!settingsForm.Visible) settingsForm.Show(this);
            settingsForm.Activate();
        }

        private void UpdateCompactPlayback()
        {
            if (popupSummary == null) return;
            popupSummary.Text = "目标缓冲 " + LatencyProfile.Resolve(selectedMode, customLatencyMs) +
                " ms · " + (calibrationProfile.Enabled ? "EQ 已启用" : "EQ 已旁路");
            playButton.Text = playing ? (streamReady ? "正在播放电脑声音" : "正在连接音响") : "播放电脑声音";
            uiTips.SetToolTip(targetDetail, SelectionSummary());
            uiTips.SetToolTip(popupStatus, statusLabel.Text);
        }

        private void PlayDeviceRow(DeviceRow row)
        {
            if (playing) { if (selectedKeys.Contains(row.SelectionKey)) StopPlayback(); return; }
            if (scanning || volumeBusy) return;
            // A checked row starts the entire checked route, not just half of a stereo pair.
            if (!row.Check.Checked)
            {
                ClearSelection();
                row.Check.Checked = true;
            }
            StartPlayback("loopback");
        }

        private static string CompactTargetTitle(ReceiverGroup group)
        {
            return group.Name.Length > 0 ? group.Name : "未知音响";
        }

        private static string CompactTargetDetail(ReceiverGroup group)
        {
            if (group.IsStereoPair) return "原生立体声对 · 2 只";
            if (group.IsIncompleteGroup) return group.StereoPairId.Length > 0
                ? "原生配对 · 成员不完整" : "疑似配对 · 信息不完整";
            string address = group.Leader?.Address ?? "";
            return (group.IsSuspectedPair ? "独立音响 · 疑似配对" : "独立音响") +
                (address.Length > 0 ? " · " + address : "");
        }

        private void UpdateCompactSize()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int rows = Math.Max(1, Math.Min(4, deviceRows.Count));
            int height = UiPixels(254) + rows * Math.Max(UiPixels(88), Font.Height * 4 + UiPixels(12));
            ClientSize = new Size(UiPixels(410), Math.Min(height, Math.Max(UiPixels(340), work.Height - UiPixels(16))));
            if (Visible) PositionPopup();
        }

        internal static Rectangle PopupBounds(Rectangle work, Size size, int gap)
        {
            int width = Math.Min(size.Width, work.Width);
            int height = Math.Min(size.Height, work.Height);
            return new Rectangle(Math.Max(work.Left, work.Right - width - gap),
                Math.Max(work.Top, work.Bottom - height - gap), width, height);
        }

        private void PositionPopup()
        {
            if (positioningPopup || IsDisposed) return;
            positioningPopup = true;
            try { Bounds = PopupBounds(Screen.FromPoint(Cursor.Position).WorkingArea, Size, UiPixels(8)); }
            finally { positioningPopup = false; }
        }

        protected override void OnDeactivate(EventArgs args)
        {
            base.OnDeactivate(args);
            if (OfflinePreview) return;
            Post(delegate
            {
                if (exiting || ContainsFocus || ActiveForm == this ||
                    (settingsForm != null && settingsForm.Visible) ||
                    (calibrationForm != null && calibrationForm.Visible)) return;
                // NotifyIcon's click arrives after Deactivate. Its handler cancels this
                // short deferred dismissal so a single click cannot hide then reopen us.
                dismissTimer.Start();
            });
        }

        protected override void OnDpiChanged(DpiChangedEventArgs args)
        {
            base.OnDpiChanged(args);
            Post(delegate { RebuildDeviceList(); PositionPopup(); });
        }

        protected override void OnVisibleChanged(EventArgs args)
        {
            base.OnVisibleChanged(args);
            if (Visible) PositionPopup();
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            if (StartInTray)
            {
                StartInTray = false;
                Post(delegate { HideToTray(); if (!OfflinePreview) Opacity = 1; });
            }
        }

        private static void ApplyRowBackground(Control root, Color color)
        {
            root.BackColor = color;
            foreach (Control child in root.Controls)
                if (child is not Button) ApplyRowBackground(child, color);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(BorderColor))
                args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }
}
