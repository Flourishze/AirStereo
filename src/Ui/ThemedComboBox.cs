using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    // The real Win32 ComboBox and ComboLBox retain input, popup and accessibility.
    // Owner drawing changes appearance only, not selection or audio settings.
    internal sealed class ThemedComboBox : ComboBox
    {
        // ComboLBox can expose its hover/caret row through CB_GETCURSEL while
        // owner-drawing. A visual check must use committed state, not that query.
        private int markedIndex = -1;
        private bool listOpen;
        [StructLayout(LayoutKind.Sequential)]
        private struct PaintInfo
        {
            internal IntPtr Dc;
            internal int Erase;
            internal NativeRect Bounds;
            internal int Restore, Incremental;
            internal long Reserved0, Reserved1, Reserved2, Reserved3;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ComboInfo
        {
            internal int Size;
            internal NativeRect Item, Button;
            internal int ButtonState;
            internal IntPtr Combo, Edit, List;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr window, out PaintInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndPaint(IntPtr window, ref PaintInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal ThemedComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Standard;
            UpdateMetrics();
        }
        private int P(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96.0));
        private void UpdateMetrics() => ItemHeight = Math.Max(P(32), Font.Height + P(12));
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }
        protected override void OnDropDown(EventArgs e)
        {
            markedIndex = SelectedIndex;
            listOpen = true;
            ApplyPopupChrome();
            base.OnDropDown(e);
            Invalidate(false);
        }
        protected override void OnDropDownClosed(EventArgs e)
        {
            listOpen = false;
            markedIndex = SelectedIndex;
            base.OnDropDownClosed(e);
            Invalidate(false);
        }
        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            if (!listOpen) markedIndex = SelectedIndex;
            base.OnSelectedIndexChanged(e);
            Invalidate(false);
        }
        protected override void OnSelectionChangeCommitted(EventArgs e)
        {
            markedIndex = SelectedIndex;
            base.OnSelectionChangeCommitted(e);
            IntPtr list = NativeListHandle;
            if (list != IntPtr.Zero) InvalidateRect(list, IntPtr.Zero, false);
            Invalidate(false);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); ApplyPopupChrome(); Invalidate(); }

        internal IntPtr NativeListHandle
        {
            get
            {
                if (!IsHandleCreated) return IntPtr.Zero;
                var info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
                return GetComboBoxInfo(Handle, ref info) ? info.List : IntPtr.Zero;
            }
        }
        private void ApplyPopupChrome()
        {
            IntPtr list = NativeListHandle;
            if (list == IntPtr.Zero) return;
            // Cosmetic DWM hints can be rejected on older Windows. Keep the
            // functional native square popup as fallback, never a custom window.
            try
            {
                int round = DesktopTheme.Current.HighContrast ? 1 : 2;
                int dark = DesktopTheme.Current.Dark && !DesktopTheme.Current.HighContrast ? 1 : 0;
                Color color = DesktopTheme.Border;
                int border = color.R | color.G << 8 | color.B << 16;
                DwmSetWindowAttribute(list, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(list, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(list, 34, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Bounds.Width < 2 || e.Bounds.Height < 2) return;
            // Compose the whole item before blitting it. Clearing the live DC
            // followed by several fills/text operations caused visible flashing.
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, e.Bounds))
            {
                DrawItemFrame(buffer.Graphics, e);
                buffer.Render(e.Graphics);
            }
        }
        private void DrawItemFrame(Graphics g, DrawItemEventArgs e)
        {
            using (var fill = new SolidBrush(DesktopTheme.Surface)) g.FillRectangle(fill, e.Bounds);
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool chosen = !edit && e.Index >= 0 && e.Index == markedIndex;
            bool hot = !edit && (e.State & DrawItemState.Selected) != 0;
            Rectangle row = Rectangle.Inflate(e.Bounds, -P(3), -P(2));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (chosen || hot)
            {
                Color fill = DesktopTheme.Current.HighContrast ? DesktopTheme.Selection :
                    UiMotion.Blend(DesktopTheme.Surface, DesktopTheme.Ink, hot ? .09F : .05F);
                using (var path = SettingsDrawing.Rounded(row, P(5)))
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (hot)
                    using (var path = SettingsDrawing.Rounded(row, P(5)))
                    using (var pen = new Pen(DesktopTheme.Border)) g.DrawPath(pen, path);
            }
            if (chosen)
                using (var pen = new Pen(DesktopTheme.Current.HighContrast ? SystemColors.HighlightText : DesktopTheme.Accent, P(3)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    int inset = Math.Min(P(8), row.Height / 3);
                    g.DrawLine(pen, row.Left + P(4), row.Top + inset, row.Left + P(4), row.Bottom - inset);
                }
            string value = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            Color ink = !Enabled ? DesktopTheme.Disabled : DesktopTheme.Current.HighContrast && (chosen || hot) ?
                SystemColors.HighlightText : DesktopTheme.Ink;
            TextRenderer.DrawText(g, value, Font,
                new Rectangle(e.Bounds.Left + P(15), e.Bounds.Top, Math.Max(1, e.Bounds.Width - P(22)), e.Bounds.Height),
                // BufferedGraphics translates non-first rows into the backing DC.
                // GDI text must retain that translation as well as the clip.
                ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
        }
        protected override void WndProc(ref Message m)
        {
            // Own ONE client paint pass instead of first displaying the native
            // light frame and then covering it via Graphics.FromHwnd.
            if (m.Msg == 0x0014) { m.Result = (IntPtr)1; return; } // WM_ERASEBKGND
            bool print = (m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero;
            if (IsHandleCreated && Width >= 20 && Height >= 4 && (m.Msg == 0x000F || print))
            {
                if (print)
                {
                    using (Graphics target = Graphics.FromHdc(m.WParam)) PaintFaceBuffered(target);
                }
                else
                {
                    IntPtr dc = BeginPaint(Handle, out PaintInfo info);
                    try { if (dc != IntPtr.Zero) using (Graphics target = Graphics.FromHdc(dc)) PaintFaceBuffered(target); }
                    finally { EndPaint(Handle, ref info); }
                }
                m.Result = IntPtr.Zero;
                return;
            }
            // All input, accessibility and popup ownership remain native.
            base.WndProc(ref m);
        }
        private void PaintFaceBuffered(Graphics target)
        {
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(target, ClientRectangle))
            {
                PaintFace(buffer.Graphics);
                buffer.Render(target);
            }
        }
        private void PaintFace(Graphics g)
        {
                // Clear this control only, not the shared parent print DC.
                using (var background = new SolidBrush(Parent?.BackColor ?? DesktopTheme.Surface))
                    g.FillRectangle(background, ClientRectangle);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color edge = Focused && ShowFocusCues ? DesktopTheme.Accent : DesktopTheme.Border;
                using (var path = SettingsDrawing.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), P(5)))
                using (var fill = new SolidBrush(DesktopTheme.Surface))
                using (var pen = new Pen(edge))
                { g.FillPath(fill, path); g.DrawPath(pen, path); }
                int arrowWidth = P(28);
                string caption = listOpen && markedIndex >= 0 && markedIndex < Items.Count ? GetItemText(Items[markedIndex]) : Text;
                TextRenderer.DrawText(g, caption, Font, new Rectangle(P(10), 0, Math.Max(1, Width - arrowWidth - P(12)), Height),
                    Enabled ? DesktopTheme.Ink : DesktopTheme.Disabled,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
                using (var arrow = new Pen(Enabled ? DesktopTheme.Muted : DesktopTheme.Disabled, Math.Max(1, P(1))))
                {
                    float x = Width - arrowWidth / 2F, y = Height / 2F, d = P(3);
                    float direction = listOpen ? -1 : 1;
                    g.DrawLines(arrow, new[] { new PointF(x - d, y - direction * d / 2),
                        new PointF(x, y + direction * d / 2), new PointF(x + d, y - direction * d / 2) });
                }
        }
    }
}
