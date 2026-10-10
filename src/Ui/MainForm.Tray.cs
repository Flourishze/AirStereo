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

        // DeviceDpi can briefly report an invalid pre-handle value while a tray popup is
        // being created or moved between monitors. Treat anything below the Windows
        // baseline as 96 DPI so the popup cannot collapse during its first layout pass.
        private int UiPixels(int value)
        {
            int dpi = Math.Max(96, DeviceDpi);
            return Math.Max(1, (int)Math.Round(value * dpi / 96.0));
        }

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
            compactRoot = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new[] { 44F, 0F, 32F, 40F, 56F, 48F })
                root.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute,
                    height == 0 ? 100 : height));

            BufferedTableLayoutPanel header = Grid(4);
            compactHeader = header;
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
            compactActions = actions;
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
            compactVolume = volume;
            volume.Card = true;
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
            volumeBar.ValueChanged += OnVolumeValueChanged;
            volumeButton = CompactIcon("应用音量到当前所选音响", Glyph.Check);
            volumeButton.Click += delegate { ApplyVolume(); };
            volume.Controls.Add(volumeBar, 1, 0);
            volume.Controls.Add(volumeValue, 2, 0);
            volume.Controls.Add(volumeButton, 3, 0);
            InitializeVolumeAdjustment(volume);
            root.Controls.Add(volume, 0, 4);

            BufferedTableLayoutPanel footer = Grid(1);
            compactFooter = footer;
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
            ApplyCompactLayout();
            UpdateRouteUi();
        }

        private void ApplyCompactLayout()
        {
            if (compactRoot == null) return;
            int text = Math.Max(16, Font.Height);
            int header = Math.Max(UiPixels(44), text + UiPixels(16));
            int detail = Math.Max(UiPixels(32), text + UiPixels(10));
            int action = Math.Max(UiPixels(40), text + UiPixels(12));
            int volume = Math.Max(UiPixels(84), text * 2 + UiPixels(38));
            int footer = Math.Max(UiPixels(48), text * 2 + UiPixels(12));

            compactRoot.RowStyles.Clear();
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, header));
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, detail));
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, action));
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, volume));
            compactRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, footer));
            compactRoot.Padding = new Padding(UiPixels(14), UiPixels(10), UiPixels(14), UiPixels(10));

            compactHeader.ColumnStyles.Clear();
            compactHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) compactHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(36)));
            compactActions.ColumnStyles.Clear();
            compactActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            compactActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(44)));
            compactVolume.Padding = new Padding(UiPixels(8), UiPixels(8), UiPixels(6), UiPixels(8));
            compactVolume.RowStyles.Clear();
            compactVolume.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            compactVolume.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(UiPixels(26), text + UiPixels(6))));
            compactVolume.ColumnStyles.Clear();
            compactVolume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(48)));
            compactVolume.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            compactVolume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(46)));
            compactVolume.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(36)));
            compactFooter.RowStyles.Clear();
            compactFooter.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            compactFooter.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            LayoutSettingsTree(compactRoot);
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
                // The settings tree is laid out in pixels from the current monitor DPI.
                // Letting WinForms run a second automatic scale pass here is what caused
                // the 175%/200% dialog to contain controls larger than their section.
                // DarkSettingsForm handles DPI changes explicitly below instead.
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.None,
                // Keep a usable logical width at high DPI.  The old 600x540 window
                // left only a narrow strip for each table column after 175%/200%
                // font scaling, so labels and buttons were compressed into each
                // other even though the controls themselves were technically docked.
                ClientSize = new Size(980, 720), MinimumSize = new Size(560, 440),
                ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen };
            // Keep the unscaled reference.  The offline DPI harness and Windows
            // text-size settings can scale the Font independently of DeviceDpi.
            // This lets the layout use the larger of the two scale signals.
            settingsBaseFontHeight = Math.Max(1, settingsForm.Font.Height);
            settingsForm.Shown += delegate { ApplySettingsLayout(true); };
            settingsForm.DpiChanged += delegate { ApplySettingsLayout(true); };
            settingsForm.FontChanged += delegate { ApplySettingsLayout(false); };
            settingsForm.SizeChanged += delegate { ApplySettingsLayout(false); };
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
            Panel audio = new Panel { BackColor = CanvasColor, Padding = new Padding(10),
                AutoScroll = true, AutoScrollMinSize = Size.Empty,
                HorizontalScroll = { Enabled = false } };
            Panel logs = new Panel { BackColor = CanvasColor, Padding = new Padding(10),
                AutoScroll = true, AutoScrollMinSize = Size.Empty,
                HorizontalScroll = { Enabled = false } };
            BufferedTableLayoutPanel options = Grid(1);
            settingsOptions = options;
            options.BackColor = CanvasColor;
            options.Dock = DockStyle.Top;
            options.AutoScroll = false;
            options.AutoSize = false;
            options.RowCount = 4;
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            options.RowStyles.Clear();
            for (int i = 0; i < 4; i++) options.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            settingsRouteBox = BuildRouteBox();
            options.Controls.Add(settingsRouteBox, 0, 0);
            options.Controls.Add(BuildSettingsLatency(), 0, 1);
            BufferedTableLayoutPanel commands = Grid(2);
            commands.Margin = new Padding(0);
            commands.BackColor = CanvasColor;
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            patternButton = NewButton("循环测试", 160, Glyph.Wave);
            patternButton.Dock = DockStyle.Fill;
            patternButton.Click += delegate { StartPlayback("pattern"); };
            calibrationButton = NewButton("均衡器与校准", 160, Glyph.Wave);
            calibrationButton.Dock = DockStyle.Fill;
            calibrationButton.Click += delegate { OpenCalibration(); };
            commands.Controls.Add(patternButton, 0, 0);
            commands.Controls.Add(calibrationButton, 1, 0);
            options.Controls.Add(commands, 0, 2);
            options.Controls.Add(new SettingsCard(BuildMuteLocalOutput()), 0, 3);
            settingsAudioPage = audio;
            audio.Controls.Add(options);
            logs.Controls.Add(BuildLogBox());
            tabs.AddPage("音频", audio);
            settingsGeneralPage = (Panel)BuildGeneralPage();
            settingsFaultPage = (Panel)BuildFaultPage();
            tabs.AddPage("常规", settingsGeneralPage);
            tabs.AddPage("故障记录", settingsFaultPage);
            tabs.AddPage("诊断日志", logs);
            tabs.AddShortcut("均衡器", OpenCalibration);
            tabs.SelectedIndexChanged += delegate { ResetSettingsPageScroll(tabs); };
            ((DarkSettingsForm)settingsForm).ContentHost.Controls.Add(tabs);
            ApplySettingsLayout(false);
        }

        private static void ResetSettingsPageScroll(SettingsTabs tabs)
        {
            if (tabs == null || tabs.SelectedIndex < 0 || tabs.SelectedIndex >= tabs.PageCount) return;
            ScrollableControl page = tabs.PageAt(tabs.SelectedIndex) as ScrollableControl;
            if (page == null || !page.AutoScroll) return;
            page.AutoScrollPosition = Point.Empty;
            page.PerformLayout();
        }

        /// <summary>
        /// Recalculates the settings page's vertical budget from the actual font.
        /// WinForms scales controls but does not recalculate TableLayoutPanel absolute
        /// rows, which is why the old fixed 142/240/52 rows collapsed at 175% and 200%.
        /// </summary>
        private void ApplySettingsLayout(bool allowInitialResize)
        {
            if (settingsForm == null || settingsOptions == null) return;
            Rectangle work = Screen.FromControl(settingsForm).WorkingArea;
            int dpi = Math.Max(96, settingsForm.DeviceDpi);
            int DpiPixels(int logical) => Math.Max(1, (int)Math.Round(logical * dpi / 96.0));
            int text = Math.Max(16, settingsForm.Font.Height);
            double fontScale = settingsBaseFontHeight > 0
                ? settingsForm.Font.Height / (double)settingsBaseFontHeight : 1.0;
            double visualScale = Math.Max(1.0, Math.Max(dpi / 96.0, fontScale));
            int VisualPixels(int logical) => Math.Max(1, (int)Math.Round(logical * visualScale));
            // Do not rely on Font.Height alone.  Windows may report a font that is
            // already scaled while a newly-created child control still has its
            // logical size.  Use the larger of the measured text and the explicit
            // DPI size for every row/padding so neither case can clip the caption.
            int routeHeight = Math.Max(DpiPixels(160), text * 7 + DpiPixels(24));
            int modeRow = Math.Max(DpiPixels(28), text + DpiPixels(8));
            int sliderRow = Math.Max(DpiPixels(34), text + DpiPixels(12));
            int valueRow = Math.Max(DpiPixels(24), text + DpiPixels(6));
            int hintRow = Math.Max(DpiPixels(42), text * 2 + DpiPixels(8));
            int sectionTop = Math.Max(DpiPixels(24), text + DpiPixels(10));
            int sectionBottom = Math.Max(DpiPixels(8), text / 2);
            // The section title/padding and its bottom margin are outside the
            // inner table.  Keep a little rounding slack as well; without it the
            // hint row ended 8–16px below its parent after WinForms scaled the
            // form, which is exactly the clipped 175%/200% symptom.
            int latencyHeight = modeRow * 3 + sliderRow + valueRow + hintRow + 88;
            int commandHeight = Math.Max(DpiPixels(52), text + DpiPixels(26));
            // The buttons share one grid, with equal half-gutters and no outside
            // margins. Their outer edges now line up with the adjacent cards.
            patternButton.Margin = new Padding(0, 0, DpiPixels(4), DpiPixels(8));
            calibrationButton.Margin = new Padding(DpiPixels(4), 0, 0, DpiPixels(8));
            int muteHeight = Math.Max(DpiPixels(86), text + DpiPixels(56));

            settingsOptions.Height = routeHeight + latencyHeight + commandHeight + muteHeight + 8;
            settingsOptions.RowStyles.Clear();
            settingsOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, routeHeight));
            settingsOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, latencyHeight));
            settingsOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, commandHeight));
            settingsOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, muteHeight + 8));

            if (settingsLatencyTable != null)
            {
                settingsLatencyTable.RowStyles.Clear();
                settingsLatencyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, modeRow * 3));
                settingsLatencyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, sliderRow));
                settingsLatencyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, valueRow));
                settingsLatencyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, hintRow));
            }
            if (settingsRouteGrid != null)
            {
                settingsRouteGrid.RowStyles.Clear();
                // Include the button's scaled height and its top margin in the
                // row budget.  Using the current child Height is unstable after
                // a resize, while a DPI-derived floor is deterministic.
                // IconButton keeps a scaled top margin.  Reserve that margin plus
                // a small bottom breathing space as part of the row; otherwise
                // the next balance row can start a few pixels before the button's
                // painted bottom at 150% and above.
                int routeRow = Math.Max(Math.Max(DpiPixels(52), VisualPixels(52)),
                    text + Math.Max(DpiPixels(20), VisualPixels(20)));
                // Derive sizes only from font/DPI, never from the previous docked
                // control Height. Feeding Height back into this calculation made
                // each resize/reopen grow the balance row by another eight pixels.
                int balanceRow = routeRow;
                settingsRouteGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, routeRow));
                settingsRouteGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, balanceRow));
                settingsRouteGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(DpiPixels(25), text + DpiPixels(8))));
                // Leave a full scaled line of slack below the route hint.  At
                // very large text settings the label's measured height can be
                // one row taller than Font.Height, and the old margin let its
                // bottom edge fall outside the section by a few pixels.
                routeHeight = routeRow + balanceRow + Math.Max(DpiPixels(25), text + DpiPixels(8)) +
                    sectionTop + sectionBottom + settingsRouteBox.Margin.Vertical;
            }
            // The section's padding/title consumes space outside the inner table.
            // Explicit minimum sizes prevent TableLayoutPanel from shrinking the
            // section back to a stale DPI-scaled height after the host is resized.
            int latencyMinimum = modeRow * 3 + sliderRow + valueRow + hintRow +
                sectionTop + sectionBottom + latencyBox.Margin.Vertical;
            latencyBox.Padding = new Padding(DpiPixels(10), sectionTop, DpiPixels(10), sectionBottom);
            latencyBox.MinimumSize = new Size(0, latencyMinimum - latencyBox.Margin.Vertical);
            settingsOptions.RowStyles[1].Height = latencyMinimum;
            if (settingsRouteBox != null)
            {
                // The route section has its own title and padding.  Give it a
                // larger floor than the inner three rows so a scaled font never
                // clips the bottom hint or the reset button.
                settingsRouteBox.Padding = new Padding(DpiPixels(10), sectionTop, DpiPixels(10), sectionBottom);
                settingsRouteBox.MinimumSize = new Size(0, routeHeight - settingsRouteBox.Margin.Vertical);
                settingsOptions.RowStyles[0].Height = routeHeight;
            }
            settingsOptions.Height = routeHeight + latencyMinimum + commandHeight + muteHeight + 8;
            LayoutGeneralCards(dpi, text);
            if (settingsFaultGrid != null)
            {
                settingsFaultGrid.RowStyles.Clear();
                settingsFaultGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(DpiPixels(28), text + DpiPixels(8))));
                settingsFaultGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                settingsFaultGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
                settingsFaultGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(DpiPixels(40), text + DpiPixels(22))));
            }

            // Make the first display of the settings dialog wider/taller on a high-DPI
            // screen, but cap it to the monitor so a laptop never opens off-screen.
            if (settingsForm.IsHandleCreated)
            {
                int minimumWidth = Math.Min(Math.Max(560, work.Width - DpiPixels(24)), DpiPixels(680));
                int minimumHeight = Math.Min(Math.Max(440, work.Height - DpiPixels(24)), DpiPixels(440));
                settingsForm.MinimumSize = new Size(minimumWidth, minimumHeight);
                if (dpi >= 144 || text >= 20)
                {
                    int width = Math.Min(work.Width - DpiPixels(24), Math.Max(settingsForm.ClientSize.Width, DpiPixels(760)));
                    int height = Math.Min(work.Height - DpiPixels(24), Math.Max(settingsForm.ClientSize.Height, DpiPixels(680)));
                    if (allowInitialResize && !settingsInitialSizeApplied ||
                        settingsForm.ClientSize.Width < minimumWidth || settingsForm.ClientSize.Height < minimumHeight)
                        settingsForm.ClientSize = new Size(Math.Max(minimumWidth, width), Math.Max(minimumHeight, height));
                }
                settingsInitialSizeApplied = true;
            }
            settingsOptions.PerformLayout();
            settingsForm.PerformLayout();
            LayoutSettingsTree(settingsForm);
            // A DPI change or a previous visit can leave the page scrolled to the
            // middle of the audio options.  That makes the section title appear
            // cut in half (especially on 175%/200% displays), even though the
            // controls below it are laid out correctly.  Start a fresh display at
            // the top; the page remains scrollable when the monitor is too short.
            if (allowInitialResize && settingsAudioPage != null)
                settingsAudioPage.AutoScrollPosition = Point.Empty;
        }

        private static void LayoutSettingsTree(Control control)
        {
            control.PerformLayout();
            foreach (Control child in control.Controls) LayoutSettingsTree(child);
        }

        private Control BuildSettingsLatency()
        {
            latencyBox = new ThemedSection("延迟");
            latencyBox.Dock = DockStyle.Fill;
            latencyBox.Margin = new Padding(0, 0, 0, 8);
            latencyBox.Padding = new Padding(10, 26, 10, 8);
            BufferedTableLayoutPanel table = Grid(1);
            settingsLatencyTable = table;
            table.RowCount = 4;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Clear();
            for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            BufferedTableLayoutPanel modes = Grid(2);
            // Two columns leave each option a predictable minimum width.  Three
            // columns looked compact at 96 DPI but clipped labels on larger text
            // settings and on systems with different CJK font metrics.
            modes.RowCount = 3;
            modes.RowStyles.Clear();
            for (int i = 0; i < 3; i++) modes.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            for (int i = 0; i < 2; i++) modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            string[] names = { "实时 · 120 ms", "标准 · 200 ms", "缓冲 · 500 ms", "稳定 · 1000 ms", "自定义" };
            latencyModes = new RadioButton[5];
            for (int i = 0; i < latencyModes.Length; i++)
            {
                LockedRadioButton mode = new LockedRadioButton { Text = names[i], Tag = (LatencyMode)i,
                    Dock = DockStyle.Fill, ForeColor = InkColor, AutoSize = false,
                    AutoEllipsis = true, Margin = new Padding(0, 1, 6, 1),
                    TextAlign = ContentAlignment.MiddleLeft };
                mode.CheckedChanged += OnLatencyModeChanged;
                latencyModes[i] = mode;
                modes.Controls.Add(mode, i % 2, i / 2);
            }
            table.Controls.Add(modes, 0, 0);
            latencyBar = new ValueSlider { Minimum = 0, Maximum = LatencyProfile.SliderSteps,
                Dock = DockStyle.Fill, Margin = new Padding(0),
                SmallChange = 10, LargeChange = 50, AccessibleName = "自定义缓冲延迟" };
            latencyBar.ValueChanged += OnLatencySlider;
            table.Controls.Add(latencyBar, 0, 1);
            latencyValue = new Label { Dock = DockStyle.Fill, ForeColor = AccentDarkColor,
                AutoEllipsis = true, Margin = new Padding(0) };
            latencyHint = new Label { Dock = DockStyle.Fill, ForeColor = MutedColor,
                AutoEllipsis = true, Margin = new Padding(0) };
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
            // The settings form is built before the main window owns a native
            // handle.  On a high-DPI monitor its inherited font can therefore be
            // one scale behind until it is shown.  Synchronize it at the last
            // possible moment, before layout and painting occur.
            if (settingsForm != null && settingsForm.Font.Size != Font.Size)
                settingsForm.Font = Font;
            ApplySettingsLayout(true);
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
            string address = group.Leader?.Address ?? "";
            if (PlaybackRoute.Independent(group)) return "独立音响" +
                (address.Length > 0 ? " · " + address : "");
            if (group.IsIncompleteGroup && group.StereoPairId.Length > 0)
                return "配对音响" + (address.Length > 0 ? " · " + address : "");
            return (group.IsSuspectedPair ? "独立音响 · 疑似配对" : "独立音响") +
                (address.Length > 0 ? " · " + address : "");
        }

        private void UpdateCompactSize()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int rows = Math.Max(1, Math.Min(4, deviceRows.Count));
            int height = UiPixels(284) + rows * Math.Max(UiPixels(88), Font.Height * 4 + UiPixels(12));
            int width = Math.Max(UiPixels(410), MinimumSize.Width);
            int availableHeight = Math.Max(UiPixels(340), work.Height - UiPixels(16));
            ClientSize = new Size(width, Math.Min(height, availableHeight));
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
            UpdateNotificationIcon();
            Post(delegate { ApplyCompactLayout(); ApplySettingsLayout(true); RebuildDeviceList(); PositionPopup(); });
        }

        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            if (compactRoot != null) ApplyCompactLayout();
            if (settingsForm != null) settingsForm.Font = Font;
        }

        protected override void OnVisibleChanged(EventArgs args)
        {
            base.OnVisibleChanged(args);
            if (!OfflinePreview) FaultStore.Default.Activity("窗口生命周期：Visible=" + Visible + "，句柄=" + IsHandleCreated + "，尺寸=" + Width + "x" + Height);
            if (Visible) PositionPopup();
        }

        protected override void OnShown(EventArgs args)
        {
            base.OnShown(args);
            if (!OfflinePreview) FaultStore.Default.Activity("窗口生命周期：OnShown，StartInTray=" + StartInTray + "，尺寸=" + Width + "x" + Height);
            EnsurePopupSize();
            if (trayIcon != null)
            {
                // Explorer may miss the first Shell_NotifyIcon call during login or
                // after Explorer restarts. Re-registering the already-configured icon
                // is safe and makes the tray entry deterministic.
                UpdateNotificationIcon();
                trayIcon.Visible = false;
                trayIcon.Visible = true;
            }
            if (StartInTray)
            {
                StartInTray = false;
                Post(delegate { HideToTray(); if (!OfflinePreview) Opacity = 1; });
            }
            else
            {
                Opacity = 1;
                PositionPopup();
                BringToFront();
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
