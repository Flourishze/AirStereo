using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    // Retains native CheckBox input, state changes and accessibility; only paints.
    internal sealed class FluentCheckBox : CheckBox
    {
        internal FluentCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs args)
        {
            Graphics g = args.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = Math.Max(13, DeviceDpi * 15 / 96);
            size = Math.Min(size, Math.Max(1, Height - 2));
            int x = CheckAlign == ContentAlignment.MiddleCenter ? (Width - size) / 2 : 1;
            int y = (Height - size) / 2;
            Color accent = Enabled ? DesktopTheme.StrongAccent : DesktopTheme.Disabled;
            using (GraphicsPath box = new GraphicsPath())
            {
                float r = Math.Max(2, DeviceDpi * 2 / 96F);
                box.AddArc(x, y, r * 2, r * 2, 180, 90);
                box.AddArc(x + size - r * 2, y, r * 2, r * 2, 270, 90);
                box.AddArc(x + size - r * 2, y + size - r * 2, r * 2, r * 2, 0, 90);
                box.AddArc(x, y + size - r * 2, r * 2, r * 2, 90, 90);
                box.CloseFigure();
                using (SolidBrush fill = new SolidBrush(Checked ? accent : BackColor))
                using (Pen border = new Pen(Checked ? accent : DesktopTheme.Muted, Math.Max(1, DeviceDpi / 96F)))
                { g.FillPath(fill, box); g.DrawPath(border, box); }
            }
            if (Checked)
                using (Pen tick = new Pen(DesktopTheme.OnAccent, Math.Max(1.6F, DeviceDpi * 1.6F / 96F))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(tick, new[] { new PointF(x + size * .23F, y + size * .5F),
                        new PointF(x + size * .44F, y + size * .72F), new PointF(x + size * .79F, y + size * .29F) });
            int textX = x + size + Math.Max(5, DeviceDpi * 6 / 96);
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(textX, 0, Math.Max(1, Width - textX), Height), ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (UseMnemonic ? TextFormatFlags.Default : TextFormatFlags.NoPrefix));
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), ForeColor, BackColor);
        }
    }
}
