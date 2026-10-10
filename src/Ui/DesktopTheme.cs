using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    internal static partial class DesktopTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string theme, string subId);

        internal static void ApplyScrollbars(Control control)
        {
            control.HandleCreated += delegate { SetNativeTheme(control); };
            SetNativeTheme(control);
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
            DesktopTheme.Attach(this);
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
                using (Pen pen = new Pen(hovered ? Color.White : DesktopTheme.Ink, Math.Max(1.3F, DeviceDpi / 80F)))
                {
                    args.Graphics.DrawLine(pen, x - radius, y - radius, x + radius, y + radius);
                    args.Graphics.DrawLine(pen, x + radius, y - radius, x - radius, y + radius);
                }
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(args.Graphics, new Rectangle(3, 3, Width - 6, Height - 6), DesktopTheme.Ink, DesktopTheme.Canvas);
            }
        }
    }

    /// <summary>Responsive settings navigation; keeps the existing page indexes and events.</summary>
    internal sealed class SettingsTabs : UserControl
    {
        private readonly List<Control> pages = new List<Control>();
        private readonly List<NavButton> buttons = new List<NavButton>();
        private readonly TableLayoutPanel navigation, root;
        private readonly Panel content, rail, body;
        private readonly Label title, pageTitle;
        private bool layingOut;
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
                selectedIndex = value;
                for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == value;
                pages[value].BringToFront();
                pageTitle.Text = value < 2 ? buttons[value].Text + "设置" : buttons[value].Text;
                RefreshTheme();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        internal SettingsTabs()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = DesktopTheme.Canvas; ForeColor = DesktopTheme.Ink; Dock = DockStyle.Fill;
            root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            rail = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            navigation = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            title = new SettingsHeading { Text = "设置", Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = DesktopTheme.Ink, BackColor = DesktopTheme.Canvas };
            rail.Controls.Add(navigation); rail.Controls.Add(title);
            body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DesktopTheme.Canvas };
            pageTitle = new SettingsHeading { Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14,0,0,0), ForeColor = DesktopTheme.Ink, BackColor = DesktopTheme.Canvas };
            body.Controls.Add(content); body.Controls.Add(pageTitle);
            root.Controls.Add(rail); root.Controls.Add(body); Controls.Add(root);
            ApplyDpiLayout();
        }
        private void ApplyDpiLayout()
        {
            if (root == null || layingOut) return;
            layingOut = true;
            try
            {
                int P(int n) => Math.Max(1, (int)Math.Round(n * Math.Max(96, DeviceDpi) / 96.0));
                int text = Math.Max(Font.Height, P(16));
                bool wide = Width >= Math.Max(P(880), text * 42);
                root.SuspendLayout(); navigation.SuspendLayout();
                root.Padding = new Padding(P(14), 0, P(10), P(10));
                root.ColumnStyles.Clear(); root.RowStyles.Clear();
                root.ColumnCount = wide ? 2 : 1; root.RowCount = wide ? 1 : 2;
                root.ColumnStyles.Add(new ColumnStyle(wide ? SizeType.Absolute : SizeType.Percent, wide ? Math.Max(P(172), text * 9) : 100));
                if (wide) root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(wide ? SizeType.Percent : SizeType.Absolute, wide ? 100 : text * 2 + P(32)));
                if (!wide) root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.SetCellPosition(rail, new TableLayoutPanelCellPosition(0,0));
                root.SetCellPosition(body, new TableLayoutPanelCellPosition(wide ? 1 : 0,wide ? 0 : 1));
                title.Height = wide ? text * 3 : 0; title.Visible = wide;
                pageTitle.Height = text * 2 + P(10);
                navigation.Padding = new Padding(0, P(8), wide ? P(10) : 0, 0);
                navigation.ColumnStyles.Clear(); navigation.RowStyles.Clear();
                int count = Math.Max(1, buttons.Count);
                navigation.ColumnCount = wide ? 1 : count;
                navigation.RowCount = wide ? count + 1 : 1;
                if (wide)
                {
                    navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    for (int i=0;i<count;i++) navigation.RowStyles.Add(new RowStyle(SizeType.Absolute,text + P(30)));
                    navigation.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                }
                else
                {
                    for (int i=0;i<count;i++) navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/count));
                    navigation.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                }
                for (int i=0;i<buttons.Count;i++)
                {
                    buttons[i].MinimumSize = Size.Empty;
                    navigation.SetCellPosition(buttons[i],new TableLayoutPanelCellPosition(wide ? 0 : i,wide ? i : 0));
                }
                navigation.ResumeLayout(true); root.ResumeLayout(true);
            }
            finally { layingOut = false; }
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyDpiLayout(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ApplyDpiLayout(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyDpiLayout(); }
        internal void RefreshTheme()
        {
            for (int i=0;i<buttons.Count;i++) { buttons[i].Selected = i == selectedIndex; buttons[i].Invalidate(); }
        }
        internal void AddPage(string name, Control page)
        {
            int index = pages.Count;
            pages.Add(page); page.Dock = DockStyle.Fill; page.Margin = new Padding(0);
            page.BackColor = DesktopTheme.Canvas; page.ForeColor = DesktopTheme.Ink; page.Visible = false;
            content.Controls.Add(page);
            NavButton button = new NavButton { Text = name, Dock = DockStyle.Fill, Margin = new Padding(0,0,0,6),
                AccessibleRole = AccessibleRole.PageTab, AccessibleName = name };
            button.Click += delegate { SelectedIndex = index; };
            button.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right && e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
                bool back = e.KeyCode == Keys.Left || e.KeyCode == Keys.Up;
                SelectedIndex = (selectedIndex + (back ? pages.Count-1 : 1)) % pages.Count;
                buttons[selectedIndex].Focus(); e.Handled = true;
            };
            buttons.Add(button); navigation.Controls.Add(button); ApplyDpiLayout();
            if (selectedIndex < 0) SelectedIndex = 0;
        }
        internal void AddShortcut(string name, Action action)
        {
            NavButton button = new NavButton { Text = name, Dock = DockStyle.Fill, Margin = new Padding(0,0,0,6),
                AccessibleName = name, AccessibleRole = AccessibleRole.PushButton };
            button.Click += delegate { action(); };
            buttons.Add(button); navigation.Controls.Add(button); ApplyDpiLayout();
        }
        private sealed class NavButton : Button
        {
            private readonly UiMotion motion;
            private bool selected;
            internal bool Selected
            {
                get => selected;
                set { selected = value; motion?.To(selected || hovered ? 1 : 0); Invalidate(); }
            }
            private bool hovered;
            internal NavButton()
            {
                motion = new UiMotion(this);
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
                BackColor = DesktopTheme.Canvas; ForeColor = DesktopTheme.Ink;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnMouseEnter(EventArgs e) { hovered=true; motion.To(1); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered=false; motion.To(Selected ? 1 : 0); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g=e.Graphics; g.Clear(DesktopTheme.Canvas); g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (Width<2 || Height<2) return;
                if (motion.Value > 0)
                    using (var path=SettingsDrawing.Rounded(new Rectangle(0,0,Width-1,Height-1),8))
                    using (var fill=new SolidBrush(UiMotion.Blend(DesktopTheme.Canvas, DesktopTheme.Surface, motion.Value))) g.FillPath(fill,path);
                if (Selected)
                    using (var pen=new Pen(DesktopTheme.Accent,Math.Max(3,DeviceDpi*3/96)))
                    { pen.StartCap=pen.EndCap=System.Drawing.Drawing2D.LineCap.Round; g.DrawLine(pen,5,Height*.32F,5,Height*.68F); }
                TextRenderer.DrawText(g,Text,Font,new Rectangle(16,0,Math.Max(1,Width-20),Height),DesktopTheme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle(2,2,Width-5,Height-5),DesktopTheme.Ink,DesktopTheme.Surface);
            }
        }
    }
}
