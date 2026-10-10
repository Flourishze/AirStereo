using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    internal static partial class UiRegressionTests
    {
        private static void VerifyDropdownHover(Action<string, bool, string> check)
        {
            try
            {
                foreach (AppearanceMode mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
                foreach (float size in new[] { 9F, 18F })
                using (var host = new Form { Opacity = 0, ShowInTaskbar = false, ClientSize = new Size(500,260) })
                using (var combo = new ThemedComboBox { Location = new Point(16,16), Width = 280 })
                using (var font = new Font("Segoe UI", size))
                {
                    DesktopTheme.SetMode(mode, false, false);
                    combo.Font = font;
                    combo.BackColor = DesktopTheme.Surface; combo.ForeColor = DesktopTheme.Ink;
                    combo.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });
                    combo.SelectedIndex = 2;
                    host.Controls.Add(combo); host.Show();
                    combo.Focus(); combo.DroppedDown = true;
                    int changes = 0, commits = 0;
                    combo.SelectedIndexChanged += delegate { changes++; };
                    combo.SelectionChangeCommitted += delegate { commits++; };
                    IntPtr list = combo.NativeListHandle;
                    int h = combo.ItemHeight;
                    for (int repeat = 0; repeat < 4; repeat++)
                        foreach (int row in new[] { 0, 1, 2, 1, 0 })
                            SendUiMessage(list, 0x0200, IntPtr.Zero, (IntPtr)(((row * h + h / 2) << 16) | 18));
                    string label = mode + " font=" + size;
                    check("dropdown native repeated hover never commits selection " + label,
                        commits == 0 && changes == 0 && (int)Field(combo, "markedIndex") == 2,
                        "changes=" + changes + "; commits=" + commits + "; temporary native hover=" + combo.SelectedIndex);
                    using (var nativeImage = new Bitmap(combo.Width, h * 3 + 4))
                    using (Graphics graphics = Graphics.FromImage(nativeImage))
                    {
                        graphics.Clear(DesktopTheme.Surface);
                        IntPtr dc = graphics.GetHdc();
                        try { SendUiMessage(list, 0x0317, dc, (IntPtr)0x16); }
                        finally { graphics.ReleaseHdc(dc); }
                        bool unique = true;
                        for (int row = 0; row < 3; row++)
                        {
                            int blue = 0;
                            for (int y = row * h + 5; y < (row + 1) * h - 5; y++)
                                for (int x = 3; x < 12; x++)
                                    if (nativeImage.GetPixel(x,y).ToArgb() == DesktopTheme.Accent.ToArgb()) blue++;
                            unique &= row == 2 ? blue > 5 : blue == 0;
                        }
                        check("dropdown real native list has one marker after repeated mouse movement " + label, unique, null);
                        CheckDropdownLabels(nativeImage, h, "native popup " + label, check);
                        string preview = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
                        if (!string.IsNullOrEmpty(preview))
                            nativeImage.Save(Path.Combine(preview,"native-hover-" + mode + "-" + size + ".png"));
                    }

                    using (var image = new Bitmap(combo.Width, h * 3))
                    using (Graphics graphics = Graphics.FromImage(image))
                    {
                        var draw = typeof(ThemedComboBox).GetMethod("OnDrawItem", Private);
                        graphics.Clear(DesktopTheme.Surface);
                        void Draw(int row, bool hover)
                        {
                            var args = new DrawItemEventArgs(graphics, combo.Font,
                                new Rectangle(0, row * h, image.Width, h), row,
                                hover ? DrawItemState.Selected : DrawItemState.None);
                            draw.Invoke(combo, new object[] { args });
                        }
                        for (int row = 0; row < 3; row++) Draw(row, row == 2);
                        bool unique = true;
                        foreach (int hover in new[] { 0, 1, 2, 1, 0, 2, 0, 1 })
                        {
                            // Mimic native partial-item painting without clearing the
                            // full list. Old hover states must not leave marker trails.
                            for (int row = 0; row < 3; row++) Draw(row, row == hover);
                            for (int row = 0; row < 3; row++)
                            {
                                bool blue = image.GetPixel(7, row * h + h / 2).ToArgb() == DesktopTheme.Accent.ToArgb();
                                unique &= blue == (row == 2);
                            }
                        }
                        check("dropdown repeated partial paints retain only committed blue marker " + label, unique, null);
                        CheckDropdownLabels(image, h, "partial paints " + label, check);
                        string directory = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
                        if (!string.IsNullOrEmpty(directory))
                        { Directory.CreateDirectory(directory); image.Save(Path.Combine(directory,"hover-" + mode + "-" + size + ".png")); }
                    }
                    int before = changes;
                    SendUiMessage(combo.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                    SendUiMessage(combo.Handle, 0x0101, (IntPtr)Keys.Escape, IntPtr.Zero);
                    check("dropdown native Escape retains committed value " + label,
                        !combo.DroppedDown && combo.SelectedIndex == 2 && changes == before && commits == 0, null);
                    combo.DroppedDown = true;
                    IntPtr position = (IntPtr)(((h + h / 2) << 16) | 18);
                    SendUiMessage(list, 0x0201, (IntPtr)1, position);
                    SendUiMessage(list, 0x0202, IntPtr.Zero, position);
                    check("dropdown native click commits exactly once " + label,
                        combo.SelectedIndex == 1 && commits == 1 && !combo.DroppedDown,
                        "value=" + combo.SelectedIndex + "; commits=" + commits);
                    // Exercise buffered face redraw repeatedly while the native input
                    // and selection remain alive. No setting events may be added.
                    int eventsBefore = changes;
                    for (int i = 0; i < 20; i++) { combo.Invalidate(false); combo.Update(); }
                    check("dropdown paint does not emit selection events " + label, changes == eventsBefore && commits == 1, null);
                    using (var face = new Bitmap(combo.Width, combo.Height))
                    {
                        combo.DrawToBitmap(face, new Rectangle(Point.Empty, face.Size));
                        int pixels = CountDropdownInk(face, new Rectangle(14,4,face.Width-48,face.Height-8));
                        check("dropdown closed caption remains visible " + label, pixels > 10, "ink pixels=" + pixels);
                        string directory = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
                        if (!string.IsNullOrEmpty(directory)) face.Save(Path.Combine(directory,"face-" + mode + "-" + size + ".png"));
                    }
                }
            }
            finally { DesktopTheme.SetMode(AppearanceMode.Dark,false,false); }
        }

        private static void CheckDropdownLabels(Bitmap image, int itemHeight, string label, Action<string, bool, string> check)
        {
            int[] counts = new int[3];
            for (int row = 0; row < counts.Length; row++)
                counts[row] = CountDropdownInk(image, new Rectangle(18,row*itemHeight+4,image.Width-48,itemHeight-8));
            check("dropdown all three item labels remain visible " + label,
                counts[0] > 10 && counts[1] > 10 && counts[2] > 10, "ink pixels=" + string.Join(",",counts));
        }

        private static int CountDropdownInk(Bitmap image, Rectangle area)
        {
            Color ink = DesktopTheme.Ink;
            int count = 0;
            area.Intersect(new Rectangle(Point.Empty,image.Size));
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    Color pixel = image.GetPixel(x,y);
                    if (Math.Abs(pixel.R-ink.R) + Math.Abs(pixel.G-ink.G) + Math.Abs(pixel.B-ink.B) < 60) count++;
                }
            return count;
        }
    }
}
