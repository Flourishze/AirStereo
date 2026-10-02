using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    internal static class DesktopTheme
    {
        internal static readonly Color Canvas = Color.FromArgb(23, 25, 29);
        internal static readonly Color Surface = Color.FromArgb(35, 38, 44);
        internal static readonly Color Ink = Color.FromArgb(239, 241, 244);
        internal static readonly Color Muted = Color.FromArgb(163, 170, 182);
        internal static readonly Color Accent = Color.FromArgb(149, 190, 255);
        internal static readonly Color Border = Color.FromArgb(63, 68, 77);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string theme, string subId);

        internal static void ApplyScrollbars(Control control)
        {
            control.HandleCreated += delegate
            {
                try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
        }
    }

    internal class DarkSettingsForm : Form
    {
        internal Panel ContentHost { get; }
        internal Button CloseWindowButton { get; }
        private readonly TitleBar title;
        private readonly TableLayoutPanel frame;
        private readonly ToolTip tips = new ToolTip();

        internal DarkSettingsForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = DesktopTheme.Canvas;
            ForeColor = DesktopTheme.Ink;
            Padding = new Padding(1);
            frame = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0),
                RowCount = 2, ColumnCount = 1, BackColor = DesktopTheme.Canvas };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            title = new TitleBar(this) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            CloseWindowButton = new CloseButton { Dock = DockStyle.Right, Width = 40,
                AccessibleName = "关闭窗口", TabStop = true };
            CloseWindowButton.Click += delegate { Close(); };
            tips.SetToolTip(CloseWindowButton, "关闭");
            title.Controls.Add(CloseWindowButton);
            ContentHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            frame.Controls.Add(title, 0, 0);
            frame.Controls.Add(ContentHost, 0, 1);
            Controls.Add(frame);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ApplyDpiLayout();
        }

        private void ApplyDpiLayout()
        {
            int dpi = Math.Max(96, DeviceDpi);
            int pixels(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96.0));
            int titleHeight = Math.Max(pixels(38), Font.Height + pixels(18));
            frame.RowStyles[0].Height = titleHeight;
            CloseWindowButton.Width = Math.Max(pixels(40), Font.Height + pixels(18));
            Padding = new Padding(Math.Max(1, pixels(1)));
            frame.PerformLayout();
        }

        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            ApplyDpiLayout();
            title?.Invalidate();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs args)
        {
            base.OnDpiChanged(args);
            ApplyDpiLayout();
        }

        protected override void OnTextChanged(EventArgs args)
        { base.OnTextChanged(args); title?.Invalidate(); }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg != 0x0084 || WindowState != FormWindowState.Normal) return;
            long coordinates = message.LParam.ToInt64();
            Point point = PointToClient(new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff)));
            int edge = Math.Max(5, DeviceDpi * 5 / 96);
            bool left = point.X < edge, right = point.X >= Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= Height - edge;
            int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) :
                left ? 10 : right ? 11 : 1;
            if (hit != 1) message.Result = new IntPtr(hit);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using (Pen border = new Pen(DesktopTheme.Border))
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        { if (disposing) tips.Dispose(); base.Dispose(disposing); }

        private sealed class TitleBar : Panel
        {
            private readonly Form owner;
            [DllImport("user32.dll")] private static extern bool ReleaseCapture();
            [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
            internal TitleBar(Form owner)
            {
                this.owner = owner;
                BackColor = DesktopTheme.Canvas;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnMouseDown(MouseEventArgs args)
            {
                base.OnMouseDown(args);
                if (args.Button == MouseButtons.Left)
                { ReleaseCapture(); SendMessage(owner.Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); }
            }
            protected override void OnPaint(PaintEventArgs args)
            {
                base.OnPaint(args);
                int inset = DeviceDpi * 12 / 96;
                int closeWidth = Controls.Count > 0 ? Controls[0].Width : 0;
                TextRenderer.DrawText(args.Graphics, owner.Text, Font,
                    new Rectangle(inset, 0, Math.Max(1, Width - inset * 2 - closeWidth), Height), DesktopTheme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        private sealed class CloseButton : Button
        {
            private bool hovered;
            internal CloseButton()
            {
                BackColor = DesktopTheme.Canvas;
                ForeColor = DesktopTheme.Ink;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnMouseEnter(EventArgs args) { hovered = true; Invalidate(); base.OnMouseEnter(args); }
            protected override void OnMouseLeave(EventArgs args) { hovered = false; Invalidate(); base.OnMouseLeave(args); }
            protected override void OnPaint(PaintEventArgs args)
            {
                args.Graphics.Clear(hovered ? Color.FromArgb(157, 52, 64) : DesktopTheme.Canvas);
                float radius = 4 * DeviceDpi / 96F;
                float x = Width / 2F, y = Height / 2F;
                using (Pen pen = new Pen(DesktopTheme.Ink, Math.Max(1.3F, DeviceDpi / 80F)))
                {
                    args.Graphics.DrawLine(pen, x - radius, y - radius, x + radius, y + radius);
                    args.Graphics.DrawLine(pen, x + radius, y - radius, x - radius, y + radius);
                }
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(args.Graphics, new Rectangle(3, 3, Width - 6, Height - 6), DesktopTheme.Ink, DesktopTheme.Canvas);
            }
        }
    }

    /// <summary>Dark navigation without the native tab control's light header and frame.</summary>
    internal sealed class SettingsTabs : UserControl
    {
        private readonly List<Control> pages = new List<Control>();
        private readonly List<Button> buttons = new List<Button>();
        private readonly TableLayoutPanel navigation;
        private readonly Panel content;
        private readonly TableLayoutPanel root;
        private int selectedIndex = -1;
        internal event EventHandler SelectedIndexChanged;
        internal int PageCount => pages.Count;
        internal Control PageAt(int index) => pages[index];
        internal Control Navigation => navigation;

        internal int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (value < 0 || value >= pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
                if (selectedIndex == value) return;
                content.SuspendLayout();
                selectedIndex = value;
                for (int i = 0; i < buttons.Count; i++)
                {
                    pages[i].Visible = i == value;
                    buttons[i].BackColor = i == value ? DesktopTheme.Surface : DesktopTheme.Canvas;
                    buttons[i].ForeColor = i == value ? DesktopTheme.Accent : DesktopTheme.Muted;
                }
                pages[value].BringToFront();
                content.ResumeLayout(true);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        internal SettingsTabs()
        {
            // The parent settings form uses an explicit DPI layout.  Inherit would
            // run another automatic scale pass over the already-sized pages when a
            // high-DPI monitor creates the handle, which is the source of the
            // compressed/clipped settings screenshots at 175% and 200%.
            AutoScaleMode = AutoScaleMode.None;
            BackColor = DesktopTheme.Canvas;
            ForeColor = DesktopTheme.Ink;
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0),
                ColumnCount = 1, RowCount = 2, BackColor = DesktopTheme.Canvas };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            navigation = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0),
                Padding = new Padding(10, 5, 10, 0), RowCount = 1, BackColor = DesktopTheme.Canvas };
            navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            root.Controls.Add(navigation, 0, 0);
            root.Controls.Add(content, 0, 1);
            Controls.Add(root);
            ApplyDpiLayout();
        }

        private void ApplyDpiLayout()
        {
            int dpi = Math.Max(96, DeviceDpi);
            int pixels(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96.0));
            root.RowStyles[0].Height = Math.Max(pixels(42), Font.Height + pixels(16));
            navigation.Padding = new Padding(pixels(10), pixels(5), pixels(10), 0);
            foreach (Button button in buttons)
                button.MinimumSize = new Size(0, Math.Max(pixels(32), Font.Height + pixels(12)));
            root.PerformLayout();
        }

        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            ApplyDpiLayout();
        }

        internal void AddPage(string name, Control page)
        {
            int index = pages.Count;
            pages.Add(page);
            page.Dock = DockStyle.Fill;
            page.Margin = new Padding(0);
            page.BackColor = DesktopTheme.Canvas;
            page.ForeColor = DesktopTheme.Ink;
            page.Visible = index == selectedIndex;
            content.Controls.Add(page);
            Button button = new Button { Text = name, Dock = DockStyle.Fill, Margin = new Padding(0),
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, ForeColor = DesktopTheme.Muted,
                BackColor = DesktopTheme.Canvas, AccessibleRole = AccessibleRole.PageTab, AccessibleName = name };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = DesktopTheme.Surface;
            button.FlatAppearance.MouseDownBackColor = DesktopTheme.Border;
            button.Click += delegate { SelectedIndex = index; };
            button.KeyDown += delegate (object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Left && args.KeyCode != Keys.Right) return;
                SelectedIndex = (selectedIndex + (args.KeyCode == Keys.Left ? pages.Count - 1 : 1)) % pages.Count;
                buttons[selectedIndex].Focus();
                args.Handled = true;
            };
            buttons.Add(button);
            navigation.ColumnCount = buttons.Count;
            navigation.ColumnStyles.Clear();
            for (int i = 0; i < buttons.Count; i++) navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / buttons.Count));
            navigation.Controls.Add(button, index, 0);
            if (selectedIndex < 0) SelectedIndex = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control page in pages) page.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
