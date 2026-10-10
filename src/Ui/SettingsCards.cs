using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    internal static class SettingsDrawing
    {
        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SettingsHeading : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using (Font heading = new Font(Font.FontFamily, Font.Size * 1.35F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, Text, heading,
                    new Rectangle(Padding.Left, 0, Math.Max(1, Width - Padding.Horizontal), Height), ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class SettingsCard : Panel
    {
        internal SettingsCard(Control content)
        {
            BackColor = DesktopTheme.Surface;
            ForeColor = DesktopTheme.Ink;
            Dock = DockStyle.Fill;
            Margin = new Padding(0, 0, 0, 10);
            Padding = new Padding(20, 14, 20, 14);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            PaintSurface(content);
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
        }
        private static void PaintSurface(Control control)
        {
            control.BackColor = DesktopTheme.Surface;
            foreach (Control child in control.Controls) PaintSurface(child);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? DesktopTheme.Canvas);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = SettingsDrawing.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(8, DeviceDpi / 8)))
            using (SolidBrush fill = new SolidBrush(DesktopTheme.Surface))
            using (Pen edge = new Pen(DesktopTheme.Border))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
        }
    }

    /// <summary>Native CheckBox state, keyboard and events, with a right-aligned switch.</summary>
    internal sealed class SettingsSwitch : CheckBox
    {
        private readonly UiMotion motion;
        internal string Description { get; set; } = "";
        internal SettingsSwitch()
        {
            motion = new UiMotion(this);
            AutoSize = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnCheckedChanged(EventArgs e)
        { motion?.To(Checked ? 1 : 0); base.OnCheckedChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Math.Max(20 * Math.Max(96, DeviceDpi) / 96, Font.Height);
            h = Math.Min(h, Math.Max(10, Height - 4));
            int w = h * 2, x = Math.Max(0, Width - w - 2), y = (Height - h) / 2;
            float amount = motion.Value;
            Color track = Enabled ? UiMotion.Blend(DesktopTheme.Border, DesktopTheme.StrongAccent, amount) : DesktopTheme.Disabled;
            using (GraphicsPath path = SettingsDrawing.Rounded(new Rectangle(x, y, w, h), h / 2))
            using (SolidBrush fill = new SolidBrush(track))
            using (SolidBrush thumb = new SolidBrush(Checked && Enabled ? DesktopTheme.OnAccent : DesktopTheme.Ink))
            {
                g.FillPath(fill, path);
                int inset = Math.Max(3, h / 6), diameter = h - inset * 2;
                g.FillEllipse(thumb, x + inset + (w - inset * 2 - diameter) * amount, y + inset, diameter, diameter);
            }
            int textWidth = Math.Max(1, x - 16);
            int titleHeight = Font.Height + 4;
            bool hint = Description.Length > 0;
            int titleTop = hint ? Math.Max(0, (Height - titleHeight - Font.Height * 2) / 2) : Math.Max(0, (Height - titleHeight) / 2);
            using (Font bold = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, bold, new Rectangle(0, titleTop, textWidth, titleHeight), ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (hint)
                TextRenderer.DrawText(g, Description, Font, new Rectangle(0, titleTop + titleHeight, textWidth,
                    Math.Max(1, Height - titleTop - titleHeight)), DesktopTheme.Muted,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), ForeColor, BackColor);
        }
    }

}
