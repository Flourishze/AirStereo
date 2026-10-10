using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AirStereo.Audio;

namespace AirStereo.Ui
{
    /// <summary>
    /// A focused tool window for the two things that need live tuning: a five band EQ and
    /// independent left/right trim. Every change is published immediately, so a stream that is
    /// already running hears the new profile on its next block without a reconnect.
    /// </summary>
    internal sealed class CalibrationForm : DarkSettingsForm
    {
        private static Color Canvas => DesktopTheme.Canvas;
        private static Color Ink => DesktopTheme.Ink;
        private static Color Muted => DesktopTheme.Muted;
        private static Color AccentDark => DesktopTheme.Accent;
        private static Color Border => DesktopTheme.Border;

        private readonly AudioProfileController controller;
        private readonly Action<AudioProfile> changed;
        private readonly CheckBox enabledBox;
        private readonly ValueSlider[] bands;
        private readonly Label[] bandValues;
        private readonly ValueSlider balanceBar;
        private readonly Label balanceValue;
        private readonly Label summary;
        private TableLayoutPanel contentLayout, bandLayout, balanceLayout;
        private Label eqFootnote;
        private Font emphasisFont;

        public CalibrationForm(AudioProfileController controller, AudioProfile initial,
            Action<AudioProfile> changed)
        {
            this.controller = controller;
            this.changed = changed;
            Text = "AirStereo · 均衡器与左右平衡";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(660, 540);
            MinimumSize = new Size(620, 430);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Canvas;
            ForeColor = Ink;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;

            TableLayoutPanel root = new TableLayoutPanel();
            contentLayout = root;
            root.Dock = DockStyle.Top;
            ContentHost.AutoScroll = true;
            root.Padding = new Padding(18, 14, 18, 12);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 208F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            enabledBox = new FluentCheckBox();
            enabledBox.Text = "启用均衡器与左右校准";
            enabledBox.Checked = initial.Enabled;
            enabledBox.ForeColor = Ink;
            enabledBox.AutoSize = true;
            enabledBox.CheckedChanged += delegate { Publish(); };

            Label enableHint = new Label();
            enableHint.Text = "关闭后完全旁路，音频不经过 DSP";
            enableHint.ForeColor = Muted;
            enableHint.AutoSize = true;


            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            enabledBox.Dock = DockStyle.Fill;
            enableHint.Dock = DockStyle.Fill;
            enableHint.TextAlign = ContentAlignment.MiddleLeft;
            header.Controls.Add(enabledBox, 0, 0);
            header.Controls.Add(enableHint, 0, 1);

            Panel eq = new Panel();
            eq.Dock = DockStyle.Fill;
            eq.BackColor = DesktopTheme.Surface;
            eq.Paint += delegate (object sender, PaintEventArgs arguments)
            {
                using (Pen pen = new Pen(Border))
                {
                    arguments.Graphics.DrawRectangle(pen, 0, 0,
                        Math.Max(1, eq.Width - 1), Math.Max(1, eq.Height - 1));
                }
            };

            TableLayoutPanel bandsTable = new TableLayoutPanel();
            bandLayout = bandsTable;
            bandsTable.Dock = DockStyle.Fill;
            bandsTable.ColumnCount = AudioProfile.BandCount;
            bandsTable.RowCount = 3;
            bandsTable.BackColor = DesktopTheme.Surface;
            bandsTable.Padding = new Padding(8, 7, 8, 0);
            bandsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            bandsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            bandsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            for (int i = 0; i < AudioProfile.BandCount; i++)
            {
                bandsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / AudioProfile.BandCount));
            }

            bands = new ValueSlider[AudioProfile.BandCount];
            bandValues = new Label[AudioProfile.BandCount];
            string[] captions = { "60 Hz", "230 Hz", "910 Hz", "3.6 kHz", "12 kHz" };
            for (int i = 0; i < bands.Length; i++)
            {
                int band = i;
                Label caption = new Label();
                caption.Text = captions[i];
                caption.TextAlign = ContentAlignment.MiddleCenter;
                caption.ForeColor = Muted;
                caption.Dock = DockStyle.Fill;

                bandValues[i] = new Label();
                bandValues[i].TextAlign = ContentAlignment.MiddleCenter;
                bandValues[i].ForeColor = AccentDark;
                bandValues[i].Dock = DockStyle.Fill;

                ValueSlider bar = new ValueSlider();
                bar.Minimum = -12;
                bar.Maximum = 12;
                bar.TickFrequency = 3;
                bar.SmallChange = 1;
                bar.LargeChange = 3;
                bar.Orientation = Orientation.Vertical;
                bar.Dock = DockStyle.Fill;
                bar.Margin = new Padding(4, 0, 4, 0);
                bar.Value = (int)Math.Round(initial.BandGainDb(i));
                bar.AccessibleName = captions[i] + " 增益（分贝）";
                bar.ValueChanged += delegate { Publish(); };
                bands[i] = bar;

                bandsTable.Controls.Add(caption, i, 0);
                bandsTable.Controls.Add(bandValues[i], i, 1);
                bandsTable.Controls.Add(bar, i, 2);
            }

            Label eqNote = new Label();
            eqFootnote = eqNote;
            eqNote.Text = "五段峰值均衡器 · 每段 ±12 dB · 调整即时生效";
            eqNote.ForeColor = Muted;
            eqNote.TextAlign = ContentAlignment.MiddleCenter;
            eqNote.Dock = DockStyle.Bottom;
            eqNote.Height = 20;
            eq.Controls.Add(bandsTable);
            eq.Controls.Add(eqNote);

            TableLayoutPanel balance = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                Padding = new Padding(10, 6, 10, 6), BackColor = DesktopTheme.Surface };
            balanceLayout = balance;
            balance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            balance.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            balance.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            balance.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            Label balanceCaption = new Label();
            balanceCaption.Text = "左右平衡：左侧偏小则向右，右侧偏小则向左；只衰减偏大的一侧";
            balanceCaption.ForeColor = Muted;
            balanceCaption.Dock = DockStyle.Fill;
            balanceCaption.Height = 22;
            balance.Controls.Add(balanceCaption, 0, 0);
            balance.SetColumnSpan(balanceCaption, 2);

            balanceValue = new Label();
            balanceValue.Dock = DockStyle.Fill;
            balanceValue.Width = 86;
            balanceValue.TextAlign = ContentAlignment.MiddleCenter;
            balanceValue.ForeColor = AccentDark;

            balanceBar = new ValueSlider();
            balanceBar.Minimum = -12;
            balanceBar.Maximum = 12;
            balanceBar.TickFrequency = 2;
            balanceBar.SmallChange = 1;
            balanceBar.LargeChange = 2;
            balanceBar.Dock = DockStyle.Fill;
            balanceBar.Value = (int)Math.Round(initial.RightGainDb - initial.LeftGainDb);
            balanceBar.AccessibleName = "均衡器左右校准（分贝）";
            balanceBar.ValueChanged += delegate { Publish(); };
            balance.Controls.Add(balanceBar, 0, 1);
            balance.Controls.Add(balanceValue, 1, 1);

            summary = new Label();
            summary.Dock = DockStyle.Fill;
            summary.ForeColor = Muted;
            summary.TextAlign = ContentAlignment.TopLeft;
            summary.Padding = new Padding(3, 8, 3, 0);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(eq, 0, 1);
            root.Controls.Add(balance, 0, 2);
            root.Controls.Add(summary, 0, 3);
            ContentHost.Controls.Add(root);
            ApplyAppearanceLayout();
            Shown += delegate { ApplyAppearanceLayout(); };
            ShowValues(initial);
        }

        private void ApplyAppearanceLayout()
        {
            if (contentLayout == null || bandLayout == null || balanceLayout == null || eqFootnote == null) return;
            int dpi = Math.Max(96, DeviceDpi);
            int P(int logical) => Math.Max(1, (int)Math.Round(logical * dpi / 96.0));
            int text = Math.Max(Font.Height, P(16));
            if (emphasisFont == null || emphasisFont.Size != Font.Size || emphasisFont.FontFamily.Name != Font.FontFamily.Name)
            {
                Font previous = emphasisFont;
                emphasisFont = new Font(Font, FontStyle.Bold);
                enabledBox.Font = emphasisFont;
                balanceValue.Font = emphasisFont;
                foreach (Label label in bandValues) label.Font = emphasisFont;
                previous?.Dispose();
            }
            contentLayout.Padding = new Padding(P(18), P(12), P(18), P(12));
            int[] heights = { text * 2 + P(18), Math.Max(P(210), text * 6 + P(90)),
                text * 3 + P(54), text * 3 + P(16) };
            contentLayout.RowStyles.Clear();
            foreach (int height in heights) contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            contentLayout.Height = heights[0] + heights[1] + heights[2] + heights[3] + contentLayout.Padding.Vertical;
            bandLayout.RowStyles[0].Height = text + P(6);
            bandLayout.RowStyles[1].Height = text + P(6);
            eqFootnote.Height = text + P(8);
            balanceLayout.ColumnStyles[1].Width = Math.Max(P(190), text * 11);
            Rectangle work = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(P(620), Math.Max(320, work.Width - P(24))),
                Math.Min(P(430), Math.Max(300, work.Height - P(24))));
            contentLayout.PerformLayout();
        }

        protected override void OnFontChanged(EventArgs args)
        { base.OnFontChanged(args); ApplyAppearanceLayout(); }
        protected override void OnDpiChanged(DpiChangedEventArgs args)
        { base.OnDpiChanged(args); ApplyAppearanceLayout(); }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { emphasisFont?.Dispose(); emphasisFont = null; }
        }

        private void Publish()
        {
            double[] gains = new double[AudioProfile.BandCount];
            for (int i = 0; i < gains.Length; i++) gains[i] = bands[i].Value;
            int balance = balanceBar.Value;
            double left = balance > 0 ? -balance : 0.0;
            double right = balance < 0 ? balance : 0.0;
            AudioProfile profile = new AudioProfile(enabledBox.Checked, gains, left, right);
            controller.Update(profile);
            ShowValues(profile);
            if (changed != null) changed(profile);
        }

        private void ShowValues(AudioProfile profile)
        {
            for (int i = 0; i < bandValues.Length; i++)
            {
                bandValues[i].Text = FormatDb(profile.BandGainDb(i));
            }
            balanceValue.Text = FormatBalance(profile.LeftGainDb, profile.RightGainDb);
            summary.Text = profile.Enabled
                ? "状态：已启用。" + (profile.IsFlat
                    ? "当前曲线平坦，听感等同旁路。"
                    : "自动前级 " + FormatDb(profile.AutoPreampDb) +
                      "，可避免正增益段削波。")
                : "状态：已旁路。不会改变左右音量，也不会增加处理延迟。";
        }

        private static string FormatDb(double value)
        {
            return (value > 0.05 ? "+" : "") + value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private static string FormatBalance(double left, double right)
        {
            if (Math.Abs(left) < 0.05 && Math.Abs(right) < 0.05) return "居中";
            return "L " + FormatDb(left) + "  R " + FormatDb(right);
        }
    }
}
