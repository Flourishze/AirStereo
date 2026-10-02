using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AirStereo.Audio;
using AirStereo.Protocol;

namespace AirStereo.Ui
{
    /// <summary>
    /// The window: scan, pick a target, play. Everything that touches the network runs on a
    /// worker thread, so the window never blocks and the stop button always answers.
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private static readonly Color CanvasColor = Color.FromArgb(23, 25, 29);
        private static readonly Color PanelColor = Color.FromArgb(35, 38, 44);
        private static readonly Color BorderColor = Color.FromArgb(63, 68, 77);
        private static readonly Color InkColor = Color.FromArgb(239, 241, 244);
        private static readonly Color MutedColor = Color.FromArgb(163, 170, 182);
        private static readonly Color AccentColor = Color.FromArgb(119, 169, 247);
        private static readonly Color AccentDarkColor = Color.FromArgb(149, 190, 255);
        private static readonly Color AccentPaleColor = Color.FromArgb(43, 54, 70);
        private static readonly Color LogBackColor = Color.FromArgb(27, 29, 34);
        private static readonly Color LogTextColor = Color.FromArgb(202, 211, 222);

        private readonly List<ReceiverGroup> groups = new List<ReceiverGroup>();
        private readonly List<DeviceRow> deviceRows = new List<DeviceRow>();
        private readonly HashSet<string> selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> selectionOrder = new List<string>();
        private readonly object logGate = new object();
        private readonly object logFileGate = new object();

        private Panel targetList;
        private Label targetDetail;
        private Button leftTestButton;
        private Button rightTestButton;
        private ValueSlider stereoBalance;
        private Button resetBalanceButton;
        private Label routeHint;
        private Label leftDeviceLabel;
        private Button scanButton;
        private Button playButton;
        private Button patternButton;
        private Button stopButton;
        private Button volumeButton;
        private Button calibrationButton;
        private ValueSlider volumeBar;
        private Label volumeValue;
        private BufferedTableLayoutPanel compactRoot;
        private BufferedTableLayoutPanel compactHeader;
        private BufferedTableLayoutPanel compactActions;
        private BufferedTableLayoutPanel compactVolume;
        private BufferedTableLayoutPanel compactFooter;
        private ThemedSection latencyBox;
        private TableLayoutPanel settingsOptions;
        private TableLayoutPanel settingsLatencyTable;
        private Control settingsRouteBox;
        private TableLayoutPanel settingsRouteGrid;
        private TableLayoutPanel settingsGeneralGrid;
        private TableLayoutPanel settingsFaultGrid;
        private Panel settingsAudioPage;
        private Panel settingsGeneralPage;
        private Panel settingsFaultPage;
        private bool settingsInitialSizeApplied;
        private int settingsBaseFontHeight;
        private RadioButton[] latencyModes;
        private ValueSlider latencyBar;
        private Label latencyValue;
        private Label latencyHint;
        private TextBox logBox;
        private ToolStripStatusLabel statusLabel;
        private NotifyIcon trayIcon;
        private Font titleFont;
        private Bitmap appIconBitmap;
        private Icon appIcon;

        private ManualResetEventSlim playStop;
        private Thread playWorker;
        private volatile bool playing;
        private volatile bool scanning;
        private volatile bool volumeBusy;
        private bool recoveringConnection;
        private bool selectionSyncing;
        private bool balanceSyncing;
        private int balancePreference;
        private LivePlaybackControl livePlayback;
        private bool streamReady;
        private string sessionState = "已选择";
        private readonly AudioProfileController calibration = new AudioProfileController();
        private AudioProfile calibrationProfile = AudioProfile.Flat;
        private CalibrationForm calibrationForm;
        /// <summary>Set only on explicit exit or shutdown, never on popup dismissal.</summary>
        private bool exiting;
        internal bool OfflinePreview { get; set; }
        private LatencyMode selectedMode = LatencyMode.Normal;
        private int customLatencyMs = LatencyProfile.NormalMs;
        /// <summary>Guards the radio buttons and the slider against echoing each other.</summary>
        private bool latencySyncing;
        private bool calibrationEnabled;
        private bool calibrationSeen;
        private string calibrationBands = "";
        private double calibrationLeft;
        private double calibrationRight;

        public MainForm()
        {
            Text = "AirStereo · 立体声 AirPlay 发送器";
            Font = PickFont();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(410, 508);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = CanvasColor;
            ForeColor = InkColor;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;

            BuildLayout();
            BuildAppIcon();
            BuildTrayIcon();
        }

        private static Font PickFont()
        {
            try { return new Font("Microsoft YaHei UI", 9F); }
            catch (ArgumentException) { return SystemFonts.MessageBoxFont; }
        }

        private void BuildAppIcon()
        {
            appIconBitmap = new Bitmap(32, 32);
            using (Graphics graphics = Graphics.FromImage(appIconBitmap))
            using (SolidBrush fill = new SolidBrush(AccentColor))
            using (Pen line = new Pen(Color.White, 2F))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(fill, 1, 1, 30, 30);
                graphics.DrawLine(line, 8, 19, 11, 19);
                graphics.DrawLine(line, 11, 19, 14, 12);
                graphics.DrawLine(line, 14, 12, 18, 23);
                graphics.DrawLine(line, 18, 23, 22, 9);
                graphics.DrawLine(line, 22, 9, 25, 19);
            }
            appIcon = Icon.FromHandle(appIconBitmap.GetHicon());
            Icon = appIcon;
        }

        /// <summary>Entry point for the window; returns a process exit code.</summary>
        public static int Run(bool startInTray = false)
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs arguments)
                {
                    ShowFailure(arguments.Exception.Message);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs args)
                {
                    if (args.ExceptionObject is Exception error)
                        FaultStore.Default.Record("未处理异常", error.Message, error.ToString(), "进程即将退出");
                };
                Application.Run(new MainForm { StartInTray = startInTray });
                return 0;
            }
            catch (Exception error)
            {
                ShowFailure(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }

        private static void ShowFailure(string message)
        {
            FaultStore.Default.Record("界面异常", message, "", "");
            MessageBox.Show(message, "AirStereo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void BuildLayout()
        {
            BuildCompactLayout();
        }

        private Control BuildHeader()
        {
            BufferedTableLayoutPanel header = new BufferedTableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);
            header.BackColor = CanvasColor;
            header.ColumnCount = 3;
            header.RowCount = 1;
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            LogoPanel logo = new LogoPanel();
            logo.Location = new Point(0, 7);
            logo.Size = new Size(44, 44);
            logo.Margin = new Padding(0, 7, 0, 0);
            header.Controls.Add(logo, 0, 0);

            BufferedTableLayoutPanel branding = new BufferedTableLayoutPanel();
            branding.Dock = DockStyle.Fill;
            branding.Margin = new Padding(0);
            branding.ColumnCount = 1;
            branding.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            branding.RowCount = 2;
            branding.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            branding.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            header.Controls.Add(branding, 1, 0);

            Label title = new Label();
            title.Text = "AirStereo";
            title.Font = SafeBold(Font, 17F);
            title.ForeColor = InkColor;
            title.Dock = DockStyle.Fill;
            title.AutoEllipsis = true;
            branding.Controls.Add(title, 0, 0);

            Label subtitle = new Label();
            subtitle.Text = "HomePod 立体声 AirPlay 发送器";
            subtitle.ForeColor = AccentDarkColor;
            subtitle.Dock = DockStyle.Fill;
            subtitle.AutoEllipsis = true;
            branding.Controls.Add(subtitle, 0, 1);

            Label note = new Label();
            note.Text = "局域网直连  ·  共用时钟  ·  最小化后继续播放";
            note.ForeColor = MutedColor;
            note.Dock = DockStyle.Fill;
            note.TextAlign = ContentAlignment.MiddleRight;
            note.AutoEllipsis = true;
            header.Controls.Add(note, 2, 0);

            return header;
        }

        /// <summary>The notification icon is the persistent entry point for the popup.</summary>
        private void BuildTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("打开设备面板");
            show.Click += delegate { RestoreFromTray(); };
            ToolStripMenuItem settings = new ToolStripMenuItem("音频设置");
            settings.Click += delegate { OpenSettings(); };
            ToolStripMenuItem stop = new ToolStripMenuItem("停止播放");
            stop.Click += delegate { StopPlayback(); };
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { ExitFromTray(); };
            menu.Items.Add(show);
            menu.Items.Add(settings);
            menu.Items.Add(stop);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = appIcon;
            trayIcon.Text = "AirStereo · 立体声 AirPlay 发送器";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.MouseClick += delegate (object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Left) return;
                dismissTimer.Stop();
                if (Visible) HideToTray(); else RestoreFromTray();
            };
            trayIcon.Visible = true;
        }

        protected override void OnResize(EventArgs arguments)
        {
            base.OnResize(arguments);
            if (WindowState != FormWindowState.Minimized || exiting || trayIcon == null) return;
            // Deferred, because hiding a window from inside the resize that asked for it upsets
            // the message loop that is still unwinding.
            Post(delegate
            {
                if (WindowState == FormWindowState.Minimized) HideToTray();
            });
        }

        private void HideToTray()
        {
            if (exiting || IsDisposed) return;
            dismissTimer.Stop();
            trayIcon.Visible = true;
            ShowInTaskbar = false;
            if (!OfflinePreview) SaveSettings();
            Hide();
        }

        private void RestoreFromTray()
        {
            if (exiting || IsDisposed) return;
            dismissTimer.Stop();
            TopMost = !(settingsForm?.Visible == true);
            ShowInTaskbar = false;
            PositionPopup();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitFromTray()
        {
            exiting = true;
            Close();
        }

        private Control BuildTargetBox()
        {
            ThemedSection box = new ThemedSection("音响列表");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 0, 8);
            box.Padding = new Padding(10, 23, 10, 8);

            Panel side = new Panel { Dock = DockStyle.Right, Width = 132,
                Padding = new Padding(10, 1, 0, 0), BackColor = PanelColor };
            scanButton = NewButton("扫描音箱", 112, Glyph.Refresh);
            scanButton.Dock = DockStyle.Top;
            scanButton.Height = 32;
            scanButton.Margin = new Padding(0);
            scanButton.Click += delegate { BeginScan(); };
            side.Controls.Add(scanButton);

            Label hint = new Label { Text = "勾选 1-2 只音响\r\n第一只 L · 第二只 R",
                ForeColor = MutedColor, Dock = DockStyle.Top, Height = 48,
                Padding = new Padding(1, 10, 0, 0) };
            side.Controls.Add(hint);
            hint.BringToFront();

            targetDetail = new Label { Dock = DockStyle.Bottom, Height = 22,
                ForeColor = MutedColor, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0), AutoEllipsis = true };

            targetList = new Panel { Dock = DockStyle.Fill, BackColor = PanelColor,
                AutoScroll = true, BorderStyle = BorderStyle.None, Padding = new Padding(0) };
            box.Controls.Add(targetList);
            box.Controls.Add(targetDetail);
            box.Controls.Add(side);
            return box;
        }

        private Control BuildRouteBox()
        {
            ThemedSection box = new ThemedSection("声道测试与平衡");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 0, 8);
            box.Padding = new Padding(10, 24, 10, 6);
            BufferedTableLayoutPanel grid = new BufferedTableLayoutPanel();
            settingsRouteGrid = grid;
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 5;
            grid.RowCount = 3;
            // Keep a small elastic gutter between the device label and the R
            // test button.  A four-column percentage grid made those controls
            // touch after WinForms multiplied the font at 125–200% DPI.
            foreach (int width in new int[] { 24, 18, 18, 26, 14 })
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            // Percent rows keep the route panel usable when Windows scales the
            // message font to 175%/200%.  Absolute rows left the slider and
            // labels fighting for the same pixels on high-DPI displays.
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 37F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

            leftTestButton = NewButton("测试完整声道", 120, Glyph.Wave);
            leftTestButton.Dock = DockStyle.Fill;
            leftTestButton.Click += delegate { StartPlayback("left-check"); };
            leftDeviceLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, ForeColor = InkColor, Margin = new Padding(0) };
            rightTestButton = NewButton("R ▶ 测试", 110, Glyph.Wave);
            rightTestButton.Dock = DockStyle.Fill;
            // Keep the test button inside its table cell at high DPI.  The
            // right-side margin used to be top-only, so TableLayoutPanel could
            // paint its bottom edge over the following balance row after the
            // 150%/175%/200% scale pass.
            rightTestButton.Margin = new Padding(8, 4, 8, 8);
            rightTestButton.Click += delegate { StartPlayback("right-check"); };
            grid.Controls.Add(leftTestButton, 0, 0);
            grid.Controls.Add(leftDeviceLabel, 1, 0);
            grid.Controls.Add(rightTestButton, 3, 0);

            BufferedTableLayoutPanel balancePanel = new BufferedTableLayoutPanel();
            balancePanel.Dock = DockStyle.Fill;
            balancePanel.BackColor = PanelColor;
            balancePanel.ColumnCount = 3;
            balancePanel.RowCount = 1;
            balancePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            balancePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22F));
            balancePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            balancePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22F));
            Label balanceLeft = new Label { Text = "L", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AccentDarkColor, Font = SafeBold(Font, 9F), Margin = new Padding(0) };
            Label balanceRight = new Label { Text = "R", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AccentDarkColor, Font = SafeBold(Font, 9F), Margin = new Padding(0) };
            stereoBalance = new ValueSlider { Minimum = -100, Maximum = 100, Value = 0,
                SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 0) };
            stereoBalance.ValueChanged += delegate
            {
                if (balanceSyncing) return;
                balancePreference = stereoBalance.Value;
                if (livePlayback != null) livePlayback.Balance = balancePreference;
                UpdateRouteUi();
            };
            balancePanel.Controls.Add(balanceLeft, 0, 0);
            balancePanel.Controls.Add(stereoBalance, 1, 0);
            balancePanel.Controls.Add(balanceRight, 2, 0);

            resetBalanceButton = NewButton("", 36, Glyph.Refresh);
            resetBalanceButton.Dock = DockStyle.Fill;
            resetBalanceButton.AccessibleName = "将左右平衡重置到中间";
            uiTips.SetToolTip(resetBalanceButton, "平衡居中");
            resetBalanceButton.Click += delegate { stereoBalance.Value = 0; };
            grid.Controls.Add(balancePanel, 0, 1);
            grid.SetColumnSpan(balancePanel, 4);
            grid.Controls.Add(resetBalanceButton, 4, 1);

            routeHint = new Label { Dock = DockStyle.Fill, ForeColor = MutedColor,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0) };
            grid.Controls.Add(routeHint, 0, 2);
            grid.SetColumnSpan(routeHint, 5);
            box.Controls.Add(grid);
            return box;
        }
        private Control BuildActionBox()
        {
            ThemedSection box = new ThemedSection("播放");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 0, 8);
            box.Padding = new Padding(10, 23, 10, 7);

            BufferedTableLayoutPanel flow = new BufferedTableLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.BackColor = PanelColor;
            flow.ColumnCount = 7;
            flow.RowCount = 2;
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            flow.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50F));
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102F));

            playButton = NewButton("播放电脑声音", 140, Glyph.Play);
            playButton.Dock = DockStyle.Fill;
            playButton.Click += delegate { StartPlayback("loopback"); };
            playButton.Font = SafeBold(Font, 9F);

            patternButton = NewButton("测试左右声道", 130, Glyph.Wave);
            patternButton.Dock = DockStyle.Fill;
            patternButton.Click += delegate { StartPlayback("pattern"); };

            stopButton = NewButton("停止", 76, Glyph.Stop);
            stopButton.Dock = DockStyle.Fill;
            stopButton.Click += delegate { StopPlayback(); };

            calibrationButton = NewButton("均衡器与平衡", 132, Glyph.Wave);
            calibrationButton.Dock = DockStyle.Fill;
            calibrationButton.Click += delegate { OpenCalibration(); };

            Label volumeCaption = new Label();
            volumeCaption.Text = "音量";
            volumeCaption.AutoSize = false;
            volumeCaption.Size = new Size(38, 30);
            volumeCaption.TextAlign = ContentAlignment.MiddleLeft;
            volumeCaption.ForeColor = MutedColor;
            volumeCaption.Margin = new Padding(0, 1, 0, 0);
            volumeCaption.Dock = DockStyle.Left;
            volumeCaption.Width = 42;

            volumeBar = new ValueSlider();
            volumeBar.AutoSize = false;
            volumeBar.Minimum = 0;
            volumeBar.Maximum = 100;
            volumeBar.Value = 65;
            volumeBar.SmallChange = 5;
            volumeBar.LargeChange = 10;
            volumeBar.Dock = DockStyle.Fill;
            volumeBar.Margin = new Padding(0, 0, 0, 0);
            volumeBar.ValueChanged += delegate
            {
                volumeValue.Text = volumeBar.Value.ToString(CultureInfo.InvariantCulture) + "%";
            };

            volumeValue = new Label();
            volumeValue.Text = "65%";
            volumeValue.AutoSize = false;
            volumeValue.Size = new Size(44, 30);
            volumeValue.TextAlign = ContentAlignment.MiddleCenter;
            volumeValue.ForeColor = AccentDarkColor;
            volumeValue.Font = SafeBold(Font, 9F);
            volumeValue.Dock = DockStyle.Right;
            volumeValue.Width = 52;

            volumeButton = NewButton("应用音量", 96, Glyph.Check);
            volumeButton.Margin = new Padding(0);
            volumeButton.Dock = DockStyle.Right;
            volumeButton.Width = 104;
            volumeButton.Click += delegate { ApplyVolume(); };

            Panel volumeRow = new Panel();
            volumeRow.Dock = DockStyle.Fill;
            volumeRow.BackColor = PanelColor;
            volumeRow.Padding = new Padding(0, 0, 0, 0);
            volumeRow.Controls.Add(volumeBar);
            volumeRow.Controls.Add(volumeValue);
            volumeRow.Controls.Add(volumeButton);
            volumeRow.Controls.Add(volumeCaption);

            flow.Controls.Add(playButton, 0, 0);
            flow.Controls.Add(patternButton, 1, 0);
            flow.Controls.Add(stopButton, 2, 0);
            flow.Controls.Add(calibrationButton, 3, 0);
            flow.Controls.Add(volumeRow, 0, 1);
            flow.SetColumnSpan(volumeRow, 7);

            Label footnote = new Label();
            footnote.Text = "播放电脑当前声音，或用测试信号确认左右声道；均衡器与平衡可随时旁路。";
            footnote.ForeColor = MutedColor;
            footnote.Dock = DockStyle.Bottom;
            footnote.Height = 18;
            footnote.Padding = new Padding(2, 1, 0, 0);

            box.Controls.Add(flow);
            box.Controls.Add(footnote);
            return box;
        }

        private static Button NewButton(string text, int width, Glyph glyph)
        {
            IconButton button = new IconButton(glyph);
            button.Text = text;
            button.AutoSize = false;
            // A scaled WinForms Button can retain its previous scaled height as
            // MinimumSize.  Dock=Fill then refuses to shrink it when a parent
            // TableLayoutPanel recomputes its rows, causing the high-DPI overlap
            // seen in the settings page.  The row, not the child, owns the size.
            button.MinimumSize = Size.Empty;
            button.Size = new Size(width, 30);
            button.Margin = new Padding(0, 4, 8, 0);
            return button;
        }

        /// <summary>
        /// The buffer gears, in the shape TuneBlade users expect: four ready made settings plus
        /// a slider for everyone who wants their own number.
        /// </summary>
        private Control BuildLatencyBox()
        {
            latencyBox = new ThemedSection("延迟");
            latencyBox.Dock = DockStyle.Fill;
            latencyBox.Margin = new Padding(0, 0, 0, 8);
            latencyBox.Padding = new Padding(10, 23, 10, 7);

            LatencyMode[] order =
            {
                LatencyMode.Realtime, LatencyMode.Normal, LatencyMode.Buffered,
                LatencyMode.Stable, LatencyMode.Custom
            };
            string[] labels =
            {
                "实时流（" + LatencyProfile.RealtimeMs + " 毫秒）",
                "标准流（" + LatencyProfile.NormalMs + " 毫秒）",
                "缓冲流（" + LatencyProfile.BufferedMs + " 毫秒）",
                "稳定流（" + LatencyProfile.StableMs + " 毫秒）",
                "自定义（拖动右边的滑块）"
            };

            Panel modes = new Panel();
            modes.Dock = DockStyle.Left;
            modes.Width = 360;
            modes.BackColor = PanelColor;

            latencyModes = new RadioButton[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                RadioButton button = new RadioButton();
                // Two columns: three rows instead of five keeps the panel short.
                int column = i / 3;
                int row = i % 3;
                button.Text = labels[i];
                button.Tag = order[i];
                button.AutoSize = false;
                button.Size = new Size(178, 21);
                button.Location = new Point(column * 178, 2 + row * 22);
                button.ForeColor = InkColor;
                button.CheckedChanged += OnLatencyModeChanged;
                latencyModes[i] = button;
                modes.Controls.Add(button);
            }

            Panel custom = new Panel();
            custom.Dock = DockStyle.Fill;
            custom.Padding = new Padding(12, 0, 4, 0);
            custom.BackColor = PanelColor;

            Label caption = new Label();
            caption.Text = "自定义缓冲　20 - 3000 毫秒";
            caption.Dock = DockStyle.Top;
            caption.Height = 20;
            caption.TextAlign = ContentAlignment.MiddleLeft;

            Panel barRow = new Panel();
            barRow.Dock = DockStyle.Top;
            barRow.Height = 46;

            latencyValue = new Label();
            latencyValue.Text = LatencyProfile.RealtimeMs + " 毫秒";
            latencyValue.Dock = DockStyle.Right;
            latencyValue.Width = 96;
            latencyValue.TextAlign = ContentAlignment.MiddleRight;
            latencyValue.Font = SafeBold(Font, 10F);
            latencyValue.ForeColor = AccentDarkColor;

            latencyBar = new ValueSlider();
            latencyBar.Dock = DockStyle.Fill;
            latencyBar.Minimum = 0;
            latencyBar.Maximum = LatencyProfile.SliderSteps;
            latencyBar.SmallChange = 10;
            latencyBar.LargeChange = 50;
            latencyBar.Value = LatencyProfile.ToSlider(customLatencyMs);
            latencyBar.ValueChanged += OnLatencySlider;

            barRow.Controls.Add(latencyBar);
            barRow.Controls.Add(latencyValue);

            latencyHint = new Label();
            latencyHint.Dock = DockStyle.Bottom;
            latencyHint.Height = 22;
            latencyHint.ForeColor = MutedColor;
            latencyHint.TextAlign = ContentAlignment.MiddleLeft;
            latencyHint.Padding = new Padding(4, 0, 0, 0);

            custom.Controls.Add(barRow);
            custom.Controls.Add(caption);

            latencyBox.Controls.Add(custom);
            latencyBox.Controls.Add(modes);
            latencyBox.Controls.Add(latencyHint);
            // Checking a button fires the handler, so the gear is applied only now that the
            // slider and the labels it drives actually exist.
            ApplyLatencySettings(selectedMode, customLatencyMs);
            return latencyBox;
        }

        private void OnLatencyModeChanged(object sender, EventArgs arguments)
        {
            if (latencySyncing) return;
            RadioButton button = sender as RadioButton;
            if (button == null || !button.Checked) return;

            // Do not disable the whole latency section while a session is running.
            // WinForms paints disabled RadioButtons with the system disabled colour,
            // which becomes nearly black against AirStereo's dark surface on some
            // Windows themes.  Keep the controls readable and simply restore the
            // negotiated value until the next playback session.
            if (playing)
            {
                RestoreLatencyControls();
                return;
            }

            selectedMode = (LatencyMode)button.Tag;
            latencySyncing = true;
            try
            {
                if (selectedMode != LatencyMode.Custom)
                {
                    customLatencyMs = LatencyProfile.Resolve(selectedMode, customLatencyMs);
                    latencyBar.Value = LatencyProfile.ToSlider(customLatencyMs);
                }
                else
                {
                    customLatencyMs = LatencyProfile.FromSlider(latencyBar.Value);
                }
            }
            finally
            {
                latencySyncing = false;
            }
            ShowLatency();
        }

        private void OnLatencySlider(object sender, EventArgs arguments)
        {
            if (latencySyncing) return;

            if (playing)
            {
                RestoreLatencyControls();
                return;
            }

            customLatencyMs = LatencyProfile.FromSlider(latencyBar.Value);

            latencySyncing = true;
            try
            {
                // Dragging the slider is how you pick a custom buffer, so say so.
                selectedMode = LatencyMode.Custom;
                for (int i = 0; i < latencyModes.Length; i++)
                {
                    latencyModes[i].Checked = (LatencyMode)latencyModes[i].Tag == LatencyMode.Custom;
                }
            }
            finally
            {
                latencySyncing = false;
            }
            ShowLatency();
        }

        private void RestoreLatencyControls()
        {
            if (latencyBar == null || latencyModes == null) return;
            latencySyncing = true;
            try
            {
                latencyBar.Value = LatencyProfile.ToSlider(
                    LatencyProfile.Resolve(selectedMode, customLatencyMs));
                for (int i = 0; i < latencyModes.Length; i++)
                    latencyModes[i].Checked = (LatencyMode)latencyModes[i].Tag == selectedMode;
            }
            finally
            {
                latencySyncing = false;
            }
            ShowLatency();
        }

        private void ShowLatency()
        {
            int milliseconds = LatencyProfile.Resolve(selectedMode, customLatencyMs);
            latencyValue.Text = milliseconds.ToString(CultureInfo.InvariantCulture) + " 毫秒";
            latencyHint.Text = LatencyAdvice(milliseconds) + "　改完从下一次播放开始生效。";
            UpdateCompactPlayback();
        }

        private static string LatencyAdvice(int milliseconds)
        {
            if (milliseconds <= 150) return "最跟手。这一档已经把两只音箱的余量用满，网络一忙就会发虚。";
            if (milliseconds <= 300) return "跟手，又留了余量；日常推荐这一档。";
            if (milliseconds <= 800) return "很稳，延迟基本听不出来。";
            if (milliseconds <= 2000) return "网络不稳时明显更抗卡，声音会晚一点。";
            return "几乎不会断，但延迟已经能听出来，看视频会对不上口型。";
        }

        /// <summary>Bold variant of a font, falling back to the original when the family has no bold face.</summary>
        private static Font SafeBold(Font basis, float size)
        {
            try { return new Font(basis.FontFamily, size, FontStyle.Bold); }
            catch (ArgumentException) { return basis; }
        }

        private Control BuildLogBox()
        {
            ThemedSection box = new ThemedSection("活动日志");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0);
            box.Padding = new Padding(10, 23, 10, 9);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.WordWrap = false;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = LogBackColor;
            logBox.ForeColor = LogTextColor;
            logBox.BorderStyle = BorderStyle.None;
            logBox.Margin = new Padding(0);
            logBox.HideSelection = false;
            DesktopTheme.ApplyScrollbars(logBox);
            try { logBox.Font = new Font("Consolas", 9F); }
            catch (ArgumentException) { }

            box.Controls.Add(logBox);
            return box;
        }

        protected override void OnLoad(EventArgs arguments)
        {
            base.OnLoad(arguments);
            if (StartInTray) Opacity = 0;
            if (OfflinePreview) return;
            titleFont = SafeBold(Font, Font.Size);
            KeyDown += OnKeyDown;
            LoadSettings();
            PositionPopup();
            BeginScan();
        }

        private void OnKeyDown(object sender, KeyEventArgs arguments)
        {
            if (arguments.KeyCode == Keys.F5) BeginScan();
            if (arguments.KeyCode == Keys.Escape && playing) StopPlayback();
            else if (arguments.KeyCode == Keys.Escape) HideToTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs arguments)
        {
            if (!exiting && arguments.CloseReason == CloseReason.UserClosing)
            {
                arguments.Cancel = true;
                HideToTray();
                return;
            }
            exiting = true;
            if (!OfflinePreview) SaveSettings();
            StopPlayback();
            // The RTSP TEARDOWN is sent by the playback worker after it observes the stop
            // signal. Wait for that cleanup before the process exits, otherwise a HomePod can
            // keep the old session in its AirPlay picker after this window is gone.
            WaitForPlaybackStop(10000);
            if (trayIcon != null) trayIcon.Visible = false;
            base.OnFormClosing(arguments);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (dismissTimer != null) dismissTimer.Dispose();
                if (settingsForm != null) settingsForm.Dispose();
                if (uiTips != null) uiTips.Dispose();
                if (statusLabel != null) statusLabel.Dispose();
                if (titleFont != null && !ReferenceEquals(titleFont, targetList.Font)) titleFont.Dispose();
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    if (trayIcon.ContextMenuStrip != null) trayIcon.ContextMenuStrip.Dispose();
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                if (appIcon != null)
                {
                    appIcon.Dispose();
                    appIcon = null;
                }
                if (appIconBitmap != null)
                {
                    appIconBitmap.Dispose();
                    appIconBitmap = null;
                }
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- discovery

        private static string DevicePreference(Receiver receiver)
        {
            return receiver.Identity;
        }

        private static string ReceiverSelectionKey(Receiver receiver)
        {
            return "device:" + DevicePreference(receiver);
        }

        private static string SelectionKey(ReceiverGroup group)
        {
            if (group == null || group.Members.Count == 0) return "";
            List<string> ids = new List<string>();
            foreach (Receiver member in group.Members) ids.Add(DevicePreference(member));
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return (group.Members.Count > 1 ? "group:" : "device:") + string.Join(",", ids);
        }

        private List<ReceiverGroup> SelectedGroups()
        {
            List<ReceiverGroup> selected = new List<ReceiverGroup>();
            foreach (string key in selectionOrder)
            {
                if (!selectedKeys.Contains(key)) continue;
                foreach (ReceiverGroup group in groups)
                {
                    if (string.Equals(SelectionKey(group), key, StringComparison.OrdinalIgnoreCase))
                    {
                        selected.Add(group);
                        break;
                    }
                }
            }
            return selected;
        }

        private PlaybackRoute SelectedRoute()
        {
            return PlaybackRoute.Resolve(SelectedGroups());
        }

        private ReceiverGroup PlaybackTarget()
        {
            return SelectedRoute()?.Target;
        }

        private void OnDeviceCheckChanged(DeviceRow row)
        {
            if (selectionSyncing) return;
            string key = row.SelectionKey;
            if (row.Check.Checked)
            {
                List<ReceiverGroup> current = SelectedGroups();
                bool isIndependent = PlaybackRoute.Independent(row.Group);
                if (!isIndependent || row.Group.IsStereoPair)
                {
                    ClearSelection();
                    selectedKeys.Add(key);
                    selectionOrder.Add(key);
                    selectionSyncing = true;
                    row.Check.Checked = true;
                    selectionSyncing = false;
                }
                else if (current.Count >= 2)
                {
                    selectionSyncing = true;
                    row.Check.Checked = false;
                    selectionSyncing = false;
                    Log("最多选择两只独立音响。");
                    UpdateSelectionUi();
                    targetDetail.Text = "最多选择两只独立音响，请先取消一只勾选。";
                    return;
                }
                else
                {
                    foreach (ReceiverGroup selected in current)
                    {
                        if (!PlaybackRoute.Independent(selected)) ClearSelection();
                    }
                    selectedKeys.Add(key);
                    selectionOrder.Add(key);
                    selectionSyncing = true;
                    row.Check.Checked = true;
                    selectionSyncing = false;
                }
            }
            else
            {
                selectedKeys.Remove(key);
                selectionOrder.RemoveAll(delegate (string value) {
                    return string.Equals(value, key, StringComparison.OrdinalIgnoreCase);
                });
            }
            UpdateSelectionUi();
        }

        private void UpdateRouteUi()
        {
            if (routeHint == null || IsDisposed) return;
            PlaybackRoute route = SelectedRoute();
            List<ReceiverGroup> selected = SelectedGroups();
            bool split = route != null && route.SplitStereo;
            bool balanceAvailable = route != null && route.SupportsBalance;
            balanceSyncing = true;
            stereoBalance.Value = route == null ? 0 : route.EffectiveBalance(balancePreference);
            balanceSyncing = false;
            ReceiverGroup first = selected.Count > 0 ? selected[0] : null;
            leftDeviceLabel.Text = selected.Count == 0 ? "未选择音响" :
                selected.Count == 1 ? (route != null && route.NativePair ? "配对 · " : "当前 · ") + first.Name :
                "L · " + selected[0].Name + "　R · " + selected[1].Name;
            leftTestButton.Text = split ? "L 测试" : "声道测试";
            rightTestButton.Text = "R 测试";
            uiTips.SetToolTip(leftTestButton, split ? "测试左音响，3 秒后恢复" : "测试完整立体声，3 秒后恢复");
            rightTestButton.Visible = split;
            string balance = balanceAvailable ? "平衡 " + stereoBalance.Value.ToString(CultureInfo.InvariantCulture) : "单设备 · 平衡居中";
            if (selected.Count == 0)
                routeHint.Text = "未选择音响";
            else if (selected.Count > 2)
                routeHint.Text = "最多选择两只独立音响";
            else if (route != null && route.NativePair)
                routeHint.Text = "原生立体声对 · 完整立体声 · " + balance;
            else if (selected.Count == 2)
                routeHint.Text = "双设备立体声 · " + balance;
            else if (first.StereoPairId.Length > 0 && !first.IsStereoPair)
                routeHint.Text = "原生配对成员不完整 · 请刷新";
            else if (first.IsSuspectedPair || first.IsIncompleteGroup)
                routeHint.Text = "疑似配对 · " + balance;
            else
                routeHint.Text = "完整立体声 · " + balance;
            UpdateDeviceRows();
            UpdateButtons();
        }

        private void UpdateSelectionUi()
        {
            sessionState = "已选择";
            UpdateRouteUi();
            targetDetail.Text = SelectionSummary();
        }

        private void ClearSelection()
        {
            selectedKeys.Clear();
            selectionOrder.Clear();
            selectionSyncing = true;
            foreach (DeviceRow row in deviceRows) row.Check.Checked = false;
            selectionSyncing = false;
        }
        private void BeginScan()
        {
            if (scanning || playing) return;
            scanning = true;
            SetStatus("扫描中…");
            UpdateButtons();
            Log("开始扫描（约 6 秒）");

            Thread worker = new Thread(delegate ()
            {
                string scanFailure = null;
                try
                {
                    Discovery found = AirStereoApi.Discover(6);
                    Apply(found);
                }
                catch (Exception error)
                {
                    Log("扫描失败：" + error.Message);
                    RecordFault("扫描失败", error.Message, error);
                    scanFailure = error.Message;
                }
                finally
                {
                    scanning = false;
                    Post(delegate
                    {
                        UpdateButtons();
                        if (scanFailure != null) SetStatus("连接失败 · 扫描失败");
                    });
                }
            });
            worker.IsBackground = true;
            worker.Name = "air-stereo-scan";
            worker.Start();
        }

        private void Apply(Discovery found)
        {
            Post(delegate
            {
                if (IsDisposed) return;
                groups.Clear();
                groups.AddRange(found.Groups);
                RebuildDeviceList();

                foreach (string warning in found.Last.Warnings) Log("警告：" + warning);

                if (found.Last.InterfacesJoined == 0)
                {
                    Log("这台电脑没有找到可用的 IPv4 网络接口，无法搜索。");
                    RecordFault("网络不可用", "未找到可用的 IPv4 网络接口");
                    SetStatus("网络不可用");
                    UpdateButtons();
                    return;
                }

                if (groups.Count == 0)
                {
                    Log("没有发现 AirPlay 音箱。确认电脑和音箱在同一个局域网，然后按 F5 重试。");
                    RecordFault("扫描未发现设备", "当前局域网没有返回可连接的 AirPlay 目标");
                    SetStatus("没有发现音箱");
                    targetDetail.Text = "";
                    UpdateButtons();
                    return;
                }

                StringBuilder summary = new StringBuilder();
                summary.Append("发现 ").Append(groups.Count).Append(" 个目标，")
                    .Append(found.Receivers.Count).Append(" 个音箱端点");
                if (found.Rounds > 1) summary.Append("（重新扫描了 ").Append(found.Rounds).Append(" 轮）");
                Log(summary.ToString());
                foreach (ReceiverGroup group in groups) Log("  " + TargetTitle(group) + "  " + TargetDetail(group));
                foreach (Receiver receiver in found.Receivers)
                    Log("  配对信息：" + receiver.Instance + " · deviceid=" + receiver.DeviceId +
                        " · gid=" + receiver.GroupId + " · tsid=" + receiver.Txt.Get("tsid") +
                        " · tsm=" + receiver.Txt.Get("tsm") + " · igl=" + receiver.Txt.Get("igl"));
                UpdateButtons();
            });
        }

        private void RebuildDeviceList()
        {
            // Older releases stored name-inferred pairs as group:id1,id2. Restore each
            // discovered endpoint separately, without interpreting that saved key as proof.
            foreach (string oldKey in new List<string>(selectionOrder))
            {
                if (!oldKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ||
                    groups.Exists(group => string.Equals(SelectionKey(group), oldKey, StringComparison.OrdinalIgnoreCase)))
                    continue;
                int position = selectionOrder.IndexOf(oldKey);
                selectionOrder.RemoveAt(position);
                selectedKeys.Remove(oldKey);
                foreach (string id in oldKey.Substring(6).Split(','))
                {
                    string memberKey = "device:" + id;
                    if (groups.Exists(group => string.Equals(SelectionKey(group), memberKey, StringComparison.OrdinalIgnoreCase)) &&
                        selectedKeys.Add(memberKey)) selectionOrder.Insert(position++, memberKey);
                }
            }
            // Preserve a checked member when two previously separate rows become one confirmed
            // group after a later mDNS round. The physical device id remains the same.
            foreach (ReceiverGroup group in groups)
            {
                if (group.Members.Count <= 1) continue;
                string groupKey = SelectionKey(group);
                if (selectedKeys.Contains(groupKey)) continue;
                string matchedMember = null;
                foreach (Receiver member in group.Members)
                {
                    string memberKey = ReceiverSelectionKey(member);
                    if (selectedKeys.Contains(memberKey)) { matchedMember = memberKey; break; }
                }
                if (matchedMember == null) continue;
                selectedKeys.Remove(matchedMember);
                int order = selectionOrder.FindIndex(delegate (string value) {
                    return string.Equals(value, matchedMember, StringComparison.OrdinalIgnoreCase);
                });
                if (order >= 0) selectionOrder[order] = groupKey;
                selectedKeys.Add(groupKey);
            }
            HashSet<string> available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ReceiverGroup group in groups) available.Add(SelectionKey(group));
            selectedKeys.RemoveWhere(delegate (string key) { return !available.Contains(key); });
            selectionOrder.RemoveAll(delegate (string key) { return !selectedKeys.Contains(key); });

            // A newly confirmed native group occupies the whole route, even if another
            // independent row was previously selected. Saved/corrupt settings cannot exceed 2.
            List<ReceiverGroup> restored = SelectedGroups();
            ReceiverGroup exclusive = restored.Find(group => !PlaybackRoute.Independent(group));
            if (exclusive != null)
            {
                selectedKeys.Clear();
                selectionOrder.Clear();
                selectedKeys.Add(SelectionKey(exclusive));
                selectionOrder.Add(SelectionKey(exclusive));
            }
            while (selectionOrder.Count > 2)
            {
                selectedKeys.Remove(selectionOrder[2]);
                selectionOrder.RemoveAt(2);
            }

            selectionSyncing = true;
            targetList.SuspendLayout();
            try
            {
                while (targetList.Controls.Count > 0) targetList.Controls[0].Dispose();
                targetList.Controls.Clear();
                deviceRows.Clear();
                if (groups.Count == 0)
                    targetList.Controls.Add(new Label { Text = "未发现音响", Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter, ForeColor = MutedColor });
                for (int index = groups.Count - 1; index >= 0; index--)
                {
                    ReceiverGroup group = groups[index];
                    DeviceRow row = CreateDeviceRow(group);
                    row.Check.Checked = selectedKeys.Contains(row.SelectionKey);
                    deviceRows.Insert(0, row);
                    targetList.Controls.Add(row.Root);
                }
            }
            finally
            {
                targetList.ResumeLayout(true);
                selectionSyncing = false;
            }
            // Size the popup when discovery changes the list, never on a checkbox click.
            UpdateCompactSize();
            UpdateSelectionUi();
        }

        private DeviceRow CreateDeviceRow(ReceiverGroup group)
        {
            DeviceRow item = new DeviceRow();
            item.Group = group;
            item.SelectionKey = SelectionKey(group);
            BufferedTableLayoutPanel row = new BufferedTableLayoutPanel();
            row.Height = Math.Max(UiPixels(88), Font.Height * 4 + UiPixels(12));
            row.Dock = DockStyle.Top;
            row.Margin = new Padding(0, 0, 0, 2);
            row.Padding = new Padding(UiPixels(8), UiPixels(6), UiPixels(8), UiPixels(6));
            row.ColumnCount = 4;
            row.RowCount = 1;
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(28)));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(38)));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiPixels(40)));

            CheckBox check = new CheckBox { Dock = DockStyle.Fill, CheckAlign = ContentAlignment.MiddleCenter,
                ThreeState = false, Margin = new Padding(0) };
            item.Check = check;
            check.CheckedChanged += delegate { OnDeviceCheckChanged(item); };
            Label role = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AccentDarkColor, Font = SafeBold(Font, 10F), Margin = new Padding(0) };
            item.Role = role;

            Panel detail = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4, 0, 6, 0), BackColor = PanelColor };
            Label name = new Label { Text = CompactTargetTitle(group), Dock = DockStyle.Top, Height = UiPixels(25),
                ForeColor = InkColor, Font = SafeBold(Font, 9F), AutoEllipsis = true };
            Label info = new Label { Text = CompactTargetDetail(group), Dock = DockStyle.Fill,
                ForeColor = MutedColor, AutoEllipsis = true };
            detail.Controls.Add(info);
            detail.Controls.Add(name);

            Label state = new Label { Text = TargetState(group), Dock = DockStyle.Bottom, Height = UiPixels(24),
                ForeColor = group.IsIncompleteGroup ? Color.FromArgb(178, 112, 26) : MutedColor,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            item.State = state;
            detail.Controls.Add(state);
            row.Controls.Add(check, 0, 0);
            row.Controls.Add(role, 1, 0);
            row.Controls.Add(detail, 2, 0);
            Button action = NewButton("", 32, Glyph.Play);
            action.Dock = DockStyle.Fill;
            action.Margin = new Padding(2, UiPixels(16), 0, UiPixels(16));
            item.Action = action;
            action.Click += delegate { PlayDeviceRow(item); };
            uiTips.SetToolTip(check, TargetTitle(group));
            uiTips.SetToolTip(detail, TargetDetail(group));
            uiTips.SetToolTip(name, TargetTitle(group) + "\r\n" + TargetDetail(group));
            uiTips.SetToolTip(info, TargetDetail(group));
            row.Controls.Add(action, 3, 0);
            item.Root = row;
            return item;
        }

        private void UpdateDeviceRows()
        {
            bool manualStereo = SelectedGroups().Count == 2;
            int first = selectionOrder.Count > 0 ? 0 : -1;
            for (int i = 0; i < selectionOrder.Count; i++)
            {
                string key = selectionOrder[i];
                foreach (DeviceRow row in deviceRows)
                {
                    if (!string.Equals(row.SelectionKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    row.Role.Text = row.Group.IsStereoPair ? "配对" :
                        manualStereo ? (i == first ? "L" : "R") : "✓";
                    row.Role.ForeColor = row.Group.IsStereoPair ? AccentDarkColor : AccentColor;
                    row.Root.BackColor = AccentPaleColor;
                    break;
                }
            }
            foreach (DeviceRow row in deviceRows)
            {
                bool selected = selectedKeys.Contains(row.SelectionKey);
                row.Check.Enabled = !playing && !scanning;
                row.State.Text = selected ? (playing ? (streamReady ? "播放中" :
                    statusLabel.Text.StartsWith("缓冲中", StringComparison.Ordinal) ? "缓冲中" :
                    recoveringConnection ? "恢复连接" : "连接中") : sessionState) : "在线 · 未连接";
                if (row.Group.IsIncompleteGroup) row.State.Text = "在线 · 成员信息不完整";
                if (row.Action is IconButton action)
                {
                    action.Symbol = playing && selected ? Glyph.Stop : Glyph.Play;
                    action.Invalidate();
                    action.Enabled = !scanning && !volumeBusy && (!playing || selected) &&
                        !(row.Group.StereoPairId.Length > 0 && !row.Group.IsStereoPair);
                    action.AccessibleName = playing && selected ? "停止当前会话" : "播放到 " + row.Group.Name;
                    uiTips.SetToolTip(action, playing && selected ? "停止当前会话" : "播放当前勾选目标；未勾选时仅选择此目标");
                }
                if (!selectedKeys.Contains(row.SelectionKey))
                {
                    row.Role.Text = "";
                    row.Root.BackColor = PanelColor;
                }
                ApplyRowBackground(row.Root, row.Root.BackColor);
            }
        }

        private static string TargetTitle(ReceiverGroup group)
        {
            string name = group.Name.Length > 0
                ? group.Name
                : (group.Members.Count > 0 ? group.Members[0].Instance : "未知音箱");
            if (group.IsStereoPair) return name + "  ·  立体声对 · 2 只";
            if (group.IsSuspectedPair) return name + "  ·  独立选择 · 疑似配对";
            if (group.IsIncompleteGroup) return name + (group.StereoPairId.Length > 0
                ? "  ·  原生配对 · 成员不完整" : "  ·  疑似配对 · 信息不完整");
            if (group.IsGroup) return name + "  ·  组合（" + group.Members.Count + " 只）";
            return name + "  ·  独立音响";
        }

        private static string TargetDetail(ReceiverGroup group)
        {
            StringBuilder text = new StringBuilder();
            foreach (Receiver member in group.Members)
            {
                if (text.Length > 0) text.Append("  ·  ");
                text.Append(member.Instance);
                if (!string.IsNullOrEmpty(member.Address)) text.Append(" · ").Append(member.Address);
                if (group.IsGroup && ReferenceEquals(member, group.Leader)) text.Append("（主）");
            }
            Receiver sample = group.Leader;
            if (sample != null && !string.IsNullOrEmpty(sample.Model))
            {
                text.Append("　").Append(sample.Model);
                if (!string.IsNullOrEmpty(sample.OsVersion)) text.Append(" · 固件 ").Append(sample.OsVersion);
            }
            return text.ToString();
        }

        private static string TargetState(ReceiverGroup group)
        {
            if (group.IsStereoPair) return "在线\r\n原生配对";
            if (group.IsSuspectedPair) return "在线\r\n疑似配对";
            if (group.IsIncompleteGroup) return "在线\r\n信息不完整";
            return "在线\r\n" + (group.IsGroup ? "组合目标" : "独立音响");
        }

        private string SelectionSummary()
        {
            List<ReceiverGroup> selected = SelectedGroups();
            if (selected.Count == 0) return "未选择音响";
            if (selected.Count == 1) return "已选择：" + TargetTitle(selected[0]);
            return "已选择 2 只：L「" + selected[0].Name + "」 · R「" + selected[1].Name + "」";
        }

        // ---------------------------------------------------------------- playback

        private void StartPlayback(string kind)
        {
            if (playing)
            {
                if ((kind == "left-check" || kind == "right-check") && streamReady && livePlayback != null)
                {
                    bool split = SelectedRoute()?.SplitStereo == true;
                    livePlayback.StartTest(split ? (kind == "left-check" ? ChannelTest.Left : ChannelTest.Right) : ChannelTest.Stereo);
                    Log("在当前会话中测试" + (split ? (kind == "left-check" ? " L" : " R") : "完整立体声") +
                        "：3 秒后恢复电脑声音，测试期间忽略平衡值。");
                    return;
                }
                Log("已经在播放了，先按「停止」。");
                return;
            }

            PlaybackRoute route = SelectedRoute();
            ReceiverGroup group = route?.Target;
            if (group == null)
            {
                Log("请先勾选一只音响，或勾选两只独立音响组成 L/R 立体声。");
                return;
            }

            List<ReceiverGroup> selected = SelectedGroups();
            if (selected.Count > 2)
            {
                Log("最多选择两只独立音响。");
                return;
            }

            if (group.Members.Count == 1 && !PlaybackRoute.Independent(group))
            {
                Log("提醒：组合目标仅找到 1 只音箱，请按 F5 重新扫描确认。");
            }

            string playKind = kind;
            if ((kind == "left-check" || kind == "right-check") && !route.SplitStereo)
                playKind = "tone";
            PlayRequest request = new PlayRequest();
            request.Kind = playKind;
            request.Mode = selectedMode;
            request.CustomLatencyMs = customLatencyMs;
            request.Gain = 1.0;
            request.Calibration = calibration;
            request.SplitStereo = route.SplitStereo;
            request.SwapChannels = false;
            request.Balance = route.EffectiveBalance(balancePreference);
            livePlayback = kind == "loopback" ? new LivePlaybackControl(route.SupportsBalance) : null;
            if (livePlayback != null) livePlayback.Balance = request.Balance;
            request.LiveControl = livePlayback;
            streamReady = false;
            if (kind == "left-check" || kind == "right-check") request.DurationMs = 3000;

            // Test tones must be audibly unambiguous. The user's balance is restored because
            // it remains in the UI and is used again by the next normal playback.
            if (kind == "left-check" || kind == "right-check") request.Balance = 0;

            ManualResetEventSlim stopSignal = new ManualResetEventSlim(false);
            playStop = stopSignal;
            playing = true;
            UpdateButtons();
            SetStatus(recoveringConnection
                ? "恢复连接…"
                : (kind == "loopback" ? "连接中…" : "正在播放测试信号…"));
            if (kind == "loopback") Log("连接状态：连接中");
            string testLabel = route.SplitStereo
                ? (kind == "left-check" ? "L" : "R")
                : "完整立体声";
            Log(kind == "pattern"
                ? "开始播放：左声道 4 秒 → 右声道 4 秒 → 两边一起 4 秒，循环"
                : kind == "loopback" ? "开始播放电脑声音到「" + TargetTitle(group) + "」"
                : "测试 " + testLabel + " 3 秒 → " + group.Name);
            if (route.NativePair)
                Log("现有立体声对：连接组内 " + group.Members.Count +
                    " 只，仍发送完整立体声音频；物理 L/R 由接收端分配，按测试音核对。");
            else if (route.SplitStereo)
                Log("自选立体声：" + group.Members[0].Instance + " = L；" +
                    group.Members[1].Instance + " = R，共用一条 RTP 时间线；硬件延迟需听测。");
            Log("缓冲 " + request.LatencyMs.ToString(CultureInfo.InvariantCulture) + " 毫秒（" +
                LatencyProfile.DisplayName(request.Mode) + "）");

            Thread worker = new Thread(delegate ()
            {
                string failure = null;
                Exception playError = null;
                PlayResult result = null;
                try
                {
                    result = AirStereoApi.Play(group, request, stopSignal, PlaybackLog);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                    playError = error;
                }
                finally
                {
                    if (ReferenceEquals(playStop, stopSignal)) playStop = null;
                    if (ReferenceEquals(playWorker, Thread.CurrentThread)) playWorker = null;
                    stopSignal.Dispose();
                    string message = failure != null
                        ? "播放中断：" + failure
                        : "播放结束：" + (result != null ? result.Summary() : "");
                    Post(delegate
                    {
                        playing = false;
                        livePlayback = null;
                        streamReady = false;
                        recoveringConnection = failure != null;
                        Log(message);
                        if (playError != null) RecordFault("播放中断", failure, playError);
                        UpdateButtons();
                        SetStatus(failure != null ? "连接失败 · " + failure : "已停止");
                    });
                }
            });
            worker.IsBackground = true;
            worker.Name = "air-stereo-play";
            playWorker = worker;
            worker.Start();
        }

        private void StopPlayback()
        {
            if (playStop != null)
            {
                Log("正在停止播放并断开音箱…");
                playStop.Set();
            }
        }

        private void PlaybackLog(string message)
        {
            Log(message);
            if (message.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("PTP unavailable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("event channel closed:", StringComparison.OrdinalIgnoreCase))
                RecordFault("连接或同步警告", message);
            if (message.StartsWith("connected:", StringComparison.OrdinalIgnoreCase))
            {
                recoveringConnection = false;
                SetStatus("缓冲中…");
            }
            else if (message.StartsWith("streaming ", StringComparison.OrdinalIgnoreCase))
                Post(delegate { streamReady = true; UpdateButtons(); SetStatus("播放中"); });
        }

        private void OpenCalibration()
        {
            if (calibrationForm != null && !calibrationForm.IsDisposed)
            {
                calibrationForm.Activate();
                return;
            }
            calibrationForm = new CalibrationForm(calibration, calibrationProfile, delegate (AudioProfile profile)
            {
                calibrationProfile = profile;
                UpdateCompactPlayback();
                Log(profile.Enabled
                    ? "校准已更新：自动前级 " + profile.AutoPreampDb.ToString("0.0", CultureInfo.InvariantCulture) +
                      " dB，左 " + profile.LeftGainDb.ToString("0.0", CultureInfo.InvariantCulture) +
                      " dB，右 " + profile.RightGainDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB"
                    : "校准已旁路：输出恢复为原始音频");
            });
            calibrationForm.FormClosed += delegate { calibrationForm = null; };
            calibrationForm.Show(this);
        }

        private void WaitForPlaybackStop(int milliseconds)
        {
            Thread worker = playWorker;
            if (worker == null || !worker.IsAlive || ReferenceEquals(worker, Thread.CurrentThread)) return;
            worker.Join(milliseconds);
        }

        private void ApplyVolume()
        {
            ReceiverGroup group = PlaybackTarget();
            if (group == null)
            {
                Log("先选好连接目标再调音量。");
                return;
            }

            int percent = volumeBar.Value;
            volumeBusy = true;
            UpdateButtons();

            Thread worker = new Thread(delegate ()
            {
                try
                {
                    string asked = "-> " + percent.ToString(CultureInfo.InvariantCulture) + "%";
                    bool everywhere = true;
                    foreach (string line in AirStereoApi.SetVolume(group, percent, Srp.DefaultPin, Log))
                    {
                        Log("音量 " + line);
                        if (!line.EndsWith(asked, StringComparison.Ordinal)) everywhere = false;
                    }
                    Log(everywhere
                        ? "音量已生效：「" + TargetTitle(group) + "」现在都是 " +
                            percent.ToString(CultureInfo.InvariantCulture) + "%（音箱自己回报的值）。"
                        : "音量请求已发出，但有音箱回报的值不是 " +
                            percent.ToString(CultureInfo.InvariantCulture) +
                            "%，上面的箭头右侧是它现在真正的音量。");
                }
                catch (Exception error)
                {
                    Log("设置音量失败：" + error.Message);
                    RecordFault("音量设置失败", error.Message, error);
                }
                finally
                {
                    volumeBusy = false;
                    Post(UpdateButtons);
                }
            });
            worker.IsBackground = true;
            worker.Name = "air-stereo-volume";
            worker.Start();
        }

        // ---------------------------------------------------------------- plumbing

        private void UpdateButtons()
        {
            if (IsDisposed) return;
            bool hasTarget = PlaybackTarget() != null;
            int selectedCount = SelectedGroups().Count;
            scanButton.Enabled = !scanning && !playing;
            playButton.Enabled = hasTarget && selectedCount <= 2 && !playing && !scanning;
            patternButton.Enabled = hasTarget && !playing && !scanning;
            stopButton.Enabled = playing;
            volumeButton.Enabled = hasTarget && !volumeBusy;
            volumeBar.Enabled = hasTarget;
            calibrationButton.Enabled = true;
            targetList.Enabled = !scanning;
            bool canBalance = SelectedRoute()?.SupportsBalance == true && !scanning && (!playing || livePlayback != null);
            stereoBalance.Enabled = canBalance;
            resetBalanceButton.Enabled = canBalance;
            bool canTest = hasTarget && !scanning && (!playing || (streamReady && livePlayback != null));
            leftTestButton.Enabled = canTest;
            rightTestButton.Enabled = canTest && SelectedRoute()?.SplitStereo == true;
            // The buffer is negotiated when the stream is set up, so it is fixed while playing.
            // Keep the latency section enabled while streaming so RadioButton text
            // does not fall back to a low-contrast system disabled colour.  Changes
            // are ignored and restored by the handlers above because the buffer is
            // negotiated during setup and cannot be changed mid-session.
            latencyBox.Enabled = true;
            latencyBar.InputLocked = playing;
            for (int i = 0; i < latencyModes.Length; i++)
                if (latencyModes[i] is LockedRadioButton locked)
                    locked.InputLocked = playing;
            UpdateDeviceRows();
            UpdateCompactPlayback();

            if (playing) return;
            if (scanning)
            {
                SetStatus("扫描中…");
                return;
            }
            if (!hasTarget) SetStatus(groups.Count == 0 ? "没有发现音箱" :
                selectedCount > 0 ? "已选择 · 配对成员信息不完整，请刷新" : "请选择音响");
            else SetStatus("已选择");
        }

        private void SetStatus(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                Post(delegate { SetStatus(message); });
                return;
            }
            statusLabel.Text = message;
            if (message.StartsWith("连接失败", StringComparison.Ordinal)) sessionState = "连接失败";
            else if (message == "已停止") sessionState = "已停止";
            if (popupStatus != null) popupStatus.Text = message;
            if (uiTips != null && popupStatus != null) uiTips.SetToolTip(popupStatus, message);
            UpdateDeviceRows();
        }

        private void Log(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                Post(delegate { Log(message); });
                return;
            }

            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            if (!OfflinePreview) FaultStore.Default.Activity(line);
            lock (logGate)
            {
                logBox.AppendText(line + Environment.NewLine);
                if (logBox.TextLength > 400000)
                {
                    logBox.Text = logBox.Text.Substring(logBox.TextLength - 200000);
                }
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
                logBox.Refresh();
            }
            try
            {
                string path = ActivityLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                lock (logFileGate)
                {
                    FileInfo file = new FileInfo(path);
                    if (file.Exists && file.Length > 2 * 1024 * 1024) File.WriteAllText(path, "");
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // Logging must never interrupt playback or shutdown.
            }
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window went away between the check and the post. ObjectDisposedException
                // is a subclass of this one, so both cases are covered.
            }
        }

        private static string SettingsPath()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "settings.txt");
            }
            catch (Exception)
            {
                return Path.Combine(AppContext.BaseDirectory, "settings.txt");
            }
        }

        private static string ActivityLogPath()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "activity.log");
            }
            catch (Exception)
            {
                return Path.Combine(AppContext.BaseDirectory, "activity.log");
            }
        }

        private void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                LatencyMode savedMode = LatencyMode.Realtime;
                int savedCustom = customLatencyMs;
                bool haveSavedMode = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (key == "selectedDevices") LoadSelectedDevices(value);
                    else if (key == "stereoBalance" && int.TryParse(value, out int balance))
                        balancePreference = Math.Max(-100, Math.Min(100, balance));
                    else if (key == "volume" && int.TryParse(value, out int volume) && volume >= 0 && volume <= 100)
                    {
                        volumeBar.Value = volume;
                    }
                    else if (key == "latencyMode" && LatencyProfile.TryParse(value, out LatencyMode mode))
                    {
                        savedMode = mode;
                        haveSavedMode = true;
                    }
                    else if (key == "latencyCustom" && int.TryParse(value, out int custom))
                    {
                        savedCustom = custom;
                    }
                    else if (key == "calibrationEnabled")
                    {
                        calibrationSeen = true;
                        calibrationEnabled = value == "1";
                    }
                    else if (key == "calibrationBands")
                    {
                        calibrationBands = value;
                    }
                    else if (key == "calibrationLeft" && double.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double leftGain))
                    {
                        calibrationLeft = leftGain;
                    }
                    else if (key == "calibrationRight" && double.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double rightGain))
                    {
                        calibrationRight = rightGain;
                    }
                }
                UpdateRouteUi();
                if (haveSavedMode) ApplyLatencySettings(savedMode, savedCustom);
                ApplySavedCalibration();
            }
            catch (Exception error)
            {
                Log("读取设置失败（忽略）：" + error.Message);
                RecordFault("设置读取失败", error.Message, error);
            }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath(),
                    "volume=" + volumeBar.Value.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "selectedDevices=" + SaveSelectedDevices() + Environment.NewLine +
                    "stereoBalance=" + balancePreference.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "latencyMode=" + LatencyProfile.ModeName(selectedMode) + Environment.NewLine +
                    "latencyCustom=" + customLatencyMs.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    "calibrationEnabled=" + (calibrationProfile.Enabled ? "1" : "0") + Environment.NewLine +
                    "calibrationBands=" + CalibrationBandsText(calibrationProfile) + Environment.NewLine +
                    "calibrationLeft=" + calibrationProfile.LeftGainDb.ToString("0.0", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "calibrationRight=" + calibrationProfile.RightGainDb.ToString("0.0", CultureInfo.InvariantCulture) +
                    Environment.NewLine);
            }
            catch (Exception error)
            {
                RecordFault("设置保存失败", error.Message, error);
            }
        }

        private void LoadSelectedDevices(string value)
        {
            selectedKeys.Clear();
            selectionOrder.Clear();
            foreach (string token in (value ?? "").Split(','))
            {
                if (token.Length == 0) continue;
                try
                {
                    string key = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                    if (key.Length > 0 && selectedKeys.Add(key)) selectionOrder.Add(key);
                }
                catch (FormatException) { }
            }
        }

        private string SaveSelectedDevices()
        {
            StringBuilder value = new StringBuilder();
            foreach (string key in selectionOrder)
            {
                if (!selectedKeys.Contains(key)) continue;
                if (value.Length > 0) value.Append(',');
                value.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(key)));
            }
            return value.ToString();
        }

        /// <summary>Puts the saved gear and slider position back on screen.</summary>
        private void ApplyLatencySettings(LatencyMode mode, int customMs)
        {
            selectedMode = mode;
            customLatencyMs = LatencyProfile.Clamp(customMs);

            latencySyncing = true;
            try
            {
                for (int i = 0; i < latencyModes.Length; i++)
                {
                    latencyModes[i].Checked = (LatencyMode)latencyModes[i].Tag == mode;
                }
                // A fixed gear parks the slider on its own number, exactly as clicking that gear
                // does, so the slider and the label never disagree.
                latencyBar.Value = LatencyProfile.ToSlider(
                    LatencyProfile.Resolve(mode, customLatencyMs));
            }
            finally
            {
                latencySyncing = false;
            }
            ShowLatency();
        }

        private void ApplySavedCalibration()
        {
            double[] gains = new double[AudioProfile.BandCount];
            string[] parts = (calibrationBands ?? "").Split(',');
            for (int i = 0; i < gains.Length; i++)
            {
                if (i < parts.Length && double.TryParse(parts[i], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double gain))
                {
                    gains[i] = gain;
                }
            }
            calibrationProfile = new AudioProfile(calibrationSeen && calibrationEnabled,
                gains, calibrationLeft, calibrationRight);
            calibration.Update(calibrationProfile);
        }

        private static string CalibrationBandsText(AudioProfile profile)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < AudioProfile.BandCount; i++)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(profile.BandGainDb(i).ToString("0.0", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private enum Glyph
        {
            Play,
            Wave,
            Stop,
            Refresh,
            Check,
            Settings,
            Hide,
            Folder,
            Export
        }

        private sealed class DeviceRow
        {
            public ReceiverGroup Group;
            public string SelectionKey;
            public Panel Root;
            public CheckBox Check;
            public Label Role;
            public Label State;
            public Button Action;
        }

        private sealed class BufferedTableLayoutPanel : TableLayoutPanel
        {
            public BufferedTableLayoutPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = PanelColor;
                Margin = new Padding(0);
                Padding = new Padding(0);
            }
        }

        /// <summary>
        /// A radio button that can reject input while remaining Enabled=true.  The
        /// standard WinForms disabled state paints text with the system disabled
        /// colour, which is very low contrast on AirStereo's dark settings surface.
        /// </summary>
        private sealed class LockedRadioButton : RadioButton
        {
            private bool inputLocked;
            internal bool InputLocked
            {
                get => inputLocked;
                set
                {
                    if (inputLocked == value) return;
                    inputLocked = value;
                    Invalidate();
                }
            }

            public LockedRadioButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnClick(EventArgs arguments)
            {
                if (InputLocked) return;
                base.OnClick(arguments);
            }

            protected override void OnPaint(PaintEventArgs arguments)
            {
                Graphics graphics = arguments.Graphics;
                graphics.Clear(Parent == null ? PanelColor : Parent.BackColor);
                int diameter = Math.Max(12, (int)Math.Round(13 * DeviceDpi / 96.0));
                int top = Math.Max(0, (Height - diameter) / 2);
                Color ring = InputLocked ? Color.FromArgb(100, 107, 119) :
                    (Checked ? AccentColor : Color.FromArgb(204, 210, 219));
                Color dot = InputLocked ? Color.FromArgb(128, 135, 147) : AccentColor;
                using (Pen pen = new Pen(ring, Math.Max(1F, DeviceDpi / 96F)))
                using (SolidBrush brush = new SolidBrush(dot))
                {
                    graphics.DrawEllipse(pen, 1, top + 1, diameter - 2, diameter - 2);
                    if (Checked)
                    {
                        int inset = Math.Max(3, diameter / 4);
                        graphics.FillEllipse(brush, inset, top + inset,
                            diameter - inset * 2, diameter - inset * 2);
                    }
                }

                Rectangle textBounds = new Rectangle(diameter + 6, 0,
                    Math.Max(1, Width - diameter - 6), Height);
                TextRenderer.DrawText(graphics, Text, Font, textBounds, ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix);
            }
        }
        private sealed class ThemedSection : Panel
        {
            private readonly string title;

            public ThemedSection(string title)
            {
                this.title = title;
                BackColor = PanelColor;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs arguments)
            {
                base.OnPaint(arguments);
                Graphics graphics = arguments.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle frame = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (GraphicsPath path = RoundedRectangle(frame, 7))
                using (Pen pen = new Pen(BorderColor, 1F))
                {
                    graphics.DrawPath(pen, path);
                }

                using (Font sectionFont = new Font(Font, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(AccentDarkColor))
                {
                    int inset = Math.Max(8, DeviceDpi * 14 / 96);
                    int titleHeight = Math.Max(22, Font.Height + Math.Max(4, DeviceDpi * 4 / 96));
                    TextRenderer.DrawText(graphics, title, sectionFont,
                        new Rectangle(inset, 1, Math.Max(1, Width - inset * 2), titleHeight),
                        AccentDarkColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
        }

        private sealed class LogoPanel : Panel
        {
            public LogoPanel()
            {
                BackColor = CanvasColor;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs arguments)
            {
                base.OnPaint(arguments);
                Graphics graphics = arguments.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush fill = new SolidBrush(AccentColor))
                using (Pen line = new Pen(Color.White, 2F))
                {
                    graphics.FillEllipse(fill, 2, 2, 40, 40);
                    graphics.DrawLine(line, 14, 26, 18, 26);
                    graphics.DrawLine(line, 18, 26, 21, 17);
                    graphics.DrawLine(line, 21, 17, 25, 31);
                    graphics.DrawLine(line, 25, 31, 29, 12);
                    graphics.DrawLine(line, 29, 12, 33, 26);
                }
            }
        }

        private sealed class IconButton : Button
        {
            public Glyph Symbol { get; set; }
            private bool hovered;
            private bool pressed;

            public bool Accent { get; set; }

            public IconButton(Glyph glyph)
            {
                Symbol = glyph;
                Accent = glyph == Glyph.Play;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.PushButton;
            }

            protected override void OnMouseEnter(EventArgs arguments)
            {
                hovered = true;
                Invalidate();
                base.OnMouseEnter(arguments);
            }

            protected override void OnMouseLeave(EventArgs arguments)
            {
                hovered = false;
                pressed = false;
                Invalidate();
                base.OnMouseLeave(arguments);
            }

            protected override void OnMouseDown(MouseEventArgs arguments)
            {
                if (arguments.Button == MouseButtons.Left) pressed = true;
                Invalidate();
                base.OnMouseDown(arguments);
            }

            protected override void OnMouseUp(MouseEventArgs arguments)
            {
                pressed = false;
                Invalidate();
                base.OnMouseUp(arguments);
            }

            protected override void OnEnabledChanged(EventArgs arguments)
            {
                Invalidate();
                base.OnEnabledChanged(arguments);
            }

            protected override void OnPaint(PaintEventArgs arguments)
            {
                Graphics graphics = arguments.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Parent != null ? Parent.BackColor : PanelColor);
                Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                Color background;
                Color foreground;
                if (!Enabled)
                {
                    background = PanelColor;
                    foreground = Color.FromArgb(99, 106, 117);
                }
                else if (Accent)
                {
                    background = pressed ? AccentDarkColor : (hovered ? AccentPaleColor : PanelColor);
                    foreground = AccentColor;
                }
                else
                {
                    background = pressed ? AccentPaleColor :
                        (hovered ? AccentPaleColor : PanelColor);
                    foreground = AccentDarkColor;
                }

                using (GraphicsPath path = RoundedRectangle(bounds, 6))
                using (SolidBrush fill = new SolidBrush(background))
                using (Pen border = new Pen(Accent ? background : BorderColor, 1F))
                {
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(border, path);
                }

                int iconSize = Math.Max(16, (int)(16 * DeviceDpi / 96F));
                int iconLeft = Text.Length == 0 ? (Width - iconSize) / 2 : (int)(10 * DeviceDpi / 96F);
                DrawGlyph(graphics, Symbol, new Rectangle(iconLeft, (Height - iconSize) / 2, iconSize, iconSize), foreground);
                if (Text.Length > 0) TextRenderer.DrawText(graphics, Text, Font,
                    new Rectangle(iconLeft + iconSize + 5, 0, Math.Max(1, Width - iconLeft - iconSize - 10), Height), foreground,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues)
                {
                    using (Pen focus = new Pen(foreground))
                    {
                        focus.DashStyle = DashStyle.Dot;
                        graphics.DrawRectangle(focus, 4, 4, Math.Max(1, Width - 9), Math.Max(1, Height - 9));
                    }
                }
            }

            private static void DrawGlyph(Graphics graphics, Glyph glyph, Rectangle bounds, Color color)
            {
                GraphicsState saved = graphics.Save();
                graphics.TranslateTransform(bounds.X, bounds.Y);
                graphics.ScaleTransform(bounds.Width / 16F, bounds.Height / 16F);
                bounds = new Rectangle(0, 0, 16, 16);
                using (Pen pen = new Pen(color, 1.8F))
                using (SolidBrush brush = new SolidBrush(color))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    if (glyph == Glyph.Play)
                    {
                        Point[] triangle =
                        {
                            new Point(bounds.X + 3, bounds.Y + 1),
                            new Point(bounds.Right - 2, bounds.Y + bounds.Height / 2),
                            new Point(bounds.X + 3, bounds.Bottom - 1)
                        };
                        graphics.FillPolygon(brush, triangle);
                    }
                    else if (glyph == Glyph.Stop)
                    {
                        graphics.FillRectangle(brush, bounds.X + 3, bounds.Y + 3, 10, 10);
                    }
                    else if (glyph == Glyph.Check)
                    {
                        graphics.DrawLine(pen, bounds.X + 2, bounds.Y + 8, bounds.X + 6, bounds.Y + 12);
                        graphics.DrawLine(pen, bounds.X + 6, bounds.Y + 12, bounds.X + 14, bounds.Y + 3);
                    }
                    else if (glyph == Glyph.Refresh)
                    {
                        graphics.DrawArc(pen, bounds.X + 2, bounds.Y + 2, 12, 12, 35, 275);
                        graphics.DrawLine(pen, bounds.X + 12, bounds.Y + 2, bounds.X + 15, bounds.Y + 2);
                        graphics.DrawLine(pen, bounds.X + 15, bounds.Y + 2, bounds.X + 14, bounds.Y + 5);
                    }
                    else if (glyph == Glyph.Settings)
                    {
                        graphics.DrawEllipse(pen, 5, 5, 6, 6);
                        graphics.DrawEllipse(pen, 2, 2, 12, 12);
                        for (int i = 0; i < 8; i++)
                        {
                            double angle = i * Math.PI / 4;
                            graphics.DrawLine(pen, (float)(8 + Math.Cos(angle) * 6), (float)(8 + Math.Sin(angle) * 6),
                                (float)(8 + Math.Cos(angle) * 8), (float)(8 + Math.Sin(angle) * 8));
                        }
                    }
                    else if (glyph == Glyph.Hide)
                    {
                        graphics.DrawLine(pen, 3, 5, 8, 10);
                        graphics.DrawLine(pen, 8, 10, 13, 5);
                    }
                    else if (glyph == Glyph.Folder)
                    {
                        graphics.DrawLines(pen, new PointF[] {
                            new PointF(1, 13), new PointF(1, 3), new PointF(6, 3),
                            new PointF(8, 5), new PointF(15, 5), new PointF(15, 13), new PointF(1, 13) });
                    }
                    else if (glyph == Glyph.Export)
                    {
                        graphics.DrawLine(pen, 8, 1, 8, 11);
                        graphics.DrawLine(pen, 4, 7, 8, 11);
                        graphics.DrawLine(pen, 8, 11, 12, 7);
                        graphics.DrawLine(pen, 2, 11, 2, 15);
                        graphics.DrawLine(pen, 2, 15, 14, 15);
                        graphics.DrawLine(pen, 14, 15, 14, 11);
                    }
                    else
                    {
                        graphics.DrawLine(pen, bounds.X + 1, bounds.Y + 9, bounds.X + 5, bounds.Y + 9);
                        graphics.DrawLine(pen, bounds.X + 5, bounds.Y + 9, bounds.X + 8, bounds.Y + 4);
                        graphics.DrawLine(pen, bounds.X + 8, bounds.Y + 4, bounds.X + 11, bounds.Y + 13);
                        graphics.DrawLine(pen, bounds.X + 11, bounds.Y + 13, bounds.X + 15, bounds.Y + 2);
                    }
                }
                graphics.Restore(saved);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

