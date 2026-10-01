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
        private static readonly Color Canvas = DesktopTheme.Canvas;
        private static readonly Color Ink = DesktopTheme.Ink;
        private static readonly Color Muted = DesktopTheme.Muted;
        private static readonly Color AccentDark = DesktopTheme.Accent;
        private static readonly Color Border = DesktopTheme.Border;

        private readonly AudioProfileController controller;
        private readonly Action<AudioProfile> changed;
        private readonly CheckBox enabledBox;
        private readonly TrackBar[] bands;
        private readonly Label[] bandValues;
        private readonly TrackBar balanceBar;
        private readonly Label balanceValue;
        private readonly Label summary;

        public CalibrationForm(AudioProfileController controller, AudioProfile initial,
            Action<AudioProfile> changed)
        {
            this.controller = controller;
            this.changed = changed;
            Text = "AirStereo · 均衡器与左右平衡";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 468);
            MinimumSize = new Size(620, 430);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Canvas;
            ForeColor = Ink;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 14, 18, 12);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 208F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            enabledBox = new CheckBox();
            enabledBox.Text = "启用均衡器与左右校准";
            enabledBox.Checked = initial.Enabled;
            enabledBox.ForeColor = Ink;
            enabledBox.AutoSize = true;
            enabledBox.Font = new Font(Font, FontStyle.Bold);
            enabledBox.CheckedChanged += delegate { Publish(); };

            Label enableHint = new Label();
            enableHint.Text = "关闭后完全旁路，音频不经过 DSP";
            enableHint.ForeColor = Muted;
            enableHint.AutoSize = true;
            enableHint.Location = new Point(230, 2);

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Resize += delegate { enableHint.Left = Math.Max(enabledBox.Right + 18, 230); };
            header.Controls.Add(enabledBox);
            header.Controls.Add(enableHint);

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

            bands = new TrackBar[AudioProfile.BandCount];
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
                bandValues[i].Font = new Font(Font, FontStyle.Bold);
                bandValues[i].Dock = DockStyle.Fill;

                TrackBar bar = new TrackBar();
                bar.Minimum = -12;
                bar.Maximum = 12;
                bar.TickFrequency = 3;
                bar.SmallChange = 1;
                bar.LargeChange = 3;
                bar.Orientation = Orientation.Vertical;
                bar.Dock = DockStyle.Fill;
                bar.Margin = new Padding(4, 0, 4, 0);
                bar.Value = (int)Math.Round(initial.BandGainDb(i));
                bar.ValueChanged += delegate { Publish(); };
                bands[i] = bar;

                bandsTable.Controls.Add(caption, i, 0);
                bandsTable.Controls.Add(bandValues[i], i, 1);
                bandsTable.Controls.Add(bar, i, 2);
            }

            Label eqNote = new Label();
            eqNote.Text = "五段峰值均衡器 · 每段 ±12 dB · 调整即时生效";
            eqNote.ForeColor = Muted;
            eqNote.TextAlign = ContentAlignment.MiddleCenter;
            eqNote.Dock = DockStyle.Bottom;
            eqNote.Height = 20;
            eq.Controls.Add(bandsTable);
            eq.Controls.Add(eqNote);

            Panel balance = new Panel();
            balance.Dock = DockStyle.Fill;
            balance.Padding = new Padding(4, 4, 4, 0);
            balance.BackColor = DesktopTheme.Surface;

            Label balanceCaption = new Label();
            balanceCaption.Text = "左右平衡：左侧偏小则向右，右侧偏小则向左；只衰减偏大的一侧";
            balanceCaption.ForeColor = Muted;
            balanceCaption.Dock = DockStyle.Top;
            balanceCaption.Height = 22;
            balance.Controls.Add(balanceCaption);

            balanceValue = new Label();
            balanceValue.Dock = DockStyle.Right;
            balanceValue.Width = 86;
            balanceValue.TextAlign = ContentAlignment.MiddleCenter;
            balanceValue.ForeColor = AccentDark;
            balanceValue.Font = new Font(Font, FontStyle.Bold);

            balanceBar = new TrackBar();
            balanceBar.Minimum = -12;
            balanceBar.Maximum = 12;
            balanceBar.TickFrequency = 2;
            balanceBar.SmallChange = 1;
            balanceBar.LargeChange = 2;
            balanceBar.Dock = DockStyle.Fill;
            balanceBar.Value = (int)Math.Round(initial.RightGainDb - initial.LeftGainDb);
            balanceBar.ValueChanged += delegate { Publish(); };
            balance.Controls.Add(balanceBar);
            balance.Controls.Add(balanceValue);

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
            ShowValues(initial);
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
