using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    internal static partial class UiRegressionTests
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendUiMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int NativeClassName(IntPtr handle, System.Text.StringBuilder name, int capacity);

        private static void PumpMotion(int milliseconds)
        {
            long until = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
        }

        private static void VerifySettingsCards(Action<string, bool, string> check)
        {
            VerifyCompactSettings(check);
            VerifyDropdownHover(check);
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false, ClientSize = new Size(420, 200) })
            using (var toggle = new SettingsSwitch { Text = "动画测试", Bounds = new Rectangle(20,20,350,70) })
            {
                host.Controls.Add(toggle);
                UiMotion motion = (UiMotion)Field(toggle, "motion");
                int changes = 0;
                toggle.CheckedChanged += delegate { changes++; };
                toggle.Checked = true;
                check("motion hidden switch updates immediately without timer", changes == 1 && motion.Value == 1 && !motion.Running, null);
                host.Show();
                toggle.Checked = false;
                check("motion emits native CheckedChanged immediately once", !toggle.Checked && changes == 2, null);
                PumpMotion(240);
                check("motion completes and stops its timer", motion.Value == 0 && !motion.Running && changes == 2, null);
                toggle.Checked = true;
                host.Hide();
                check("motion hiding parent ends animation immediately", !motion.Running && motion.Value == 1, null);
                host.Show();
                DesktopTheme.SetMode(AppearanceMode.Dark, false, true);
                toggle.Checked = false;
                check("motion high contrast bypasses animation", !motion.Running && motion.Value == 0, null);
                DesktopTheme.SetMode(AppearanceMode.Dark, false, false);
                toggle.Checked = true;
                toggle.Dispose();
                check("motion control disposal releases timer", !motion.Running && Field(motion,"timer") == null, null);
            }
            using (var form = new MainForm { OfflinePreview=true })
            {
                Form settings=(Form)Field(form,"settingsForm");
                settings.Opacity=0; settings.Show();
                var tabs=(SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                tabs.SelectedIndex=1;
                var choice=(ComboBox)Field(form,"appearanceChoice");
                check("settings appearance uses native ComboBox without custom popup",
                    choice is ThemedComboBox themed && themed.NativeListHandle != IntPtr.Zero &&
                    choice.DrawMode==DrawMode.OwnerDrawFixed &&
                    choice.DropDownStyle==ComboBoxStyle.DropDownList,null);
                int changes=0; choice.SelectedIndexChanged+=delegate { changes++; };
                choice.SelectedIndex=(int)AppearanceMode.Light;
                check("settings native selection retains existing theme event",changes==1 && DesktopTheme.Mode==AppearanceMode.Light,null);
                choice.Focus(); choice.DroppedDown=true;
                check("settings native dropdown opens",choice.DroppedDown,null);
                choice.DroppedDown=false;
                check("settings native dropdown closes without another setting change",!choice.DroppedDown && changes==1,null);
                foreach (AppearanceMode mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
                {
                    choice.SelectedIndex = (int)mode;
                    choice.DroppedDown = true;
                    IntPtr nativeList = ((ThemedComboBox)choice).NativeListHandle;
                    var className = new System.Text.StringBuilder(64);
                    NativeClassName(nativeList, className, className.Capacity);
                    check("settings themed dropdown is native ComboLBox " + mode,
                        choice.DroppedDown && className.ToString() == "ComboLBox", className.ToString());
                    string preview = Environment.GetEnvironmentVariable("AIRSTEREO_UI_PREVIEW_DIR");
                    if (!string.IsNullOrEmpty(preview))
                    {
                        Directory.CreateDirectory(preview);
                        using (var image = new Bitmap(choice.Width, choice.ItemHeight * choice.Items.Count + 4))
                        using (Graphics graphics = Graphics.FromImage(image))
                        {
                            graphics.Clear(DesktopTheme.Surface);
                            IntPtr dc = graphics.GetHdc();
                            try { SendUiMessage(nativeList, 0x0317, dc, (IntPtr)0x16); }
                            finally { graphics.ReleaseHdc(dc); }
                            image.Save(Path.Combine(preview, "native-dropdown-" + mode + ".png"));
                        }
                    }
                    choice.DroppedDown = false;
                }
            }
            DesktopTheme.SetMode(AppearanceMode.Dark,false,false);
            using (var host = new Form { Opacity=0, ShowInTaskbar=false, ClientSize=new Size(980,720) })
            using (var tabs = new SettingsTabs())
            {
                tabs.AddPage("音频",new Panel()); tabs.AddPage("常规",new Panel());
                tabs.AddPage("故障记录",new Panel()); tabs.AddPage("诊断日志",new Panel());
                int invoked=0; tabs.AddShortcut("均衡器",()=>invoked++);
                host.Controls.Add(tabs); host.Show();
                var nav=(TableLayoutPanel)tabs.Navigation;
                check("settings wide window uses left navigation",nav.ColumnCount==1 && nav.RowCount==6,null);
                ((Button)nav.Controls[1]).PerformClick();
                ((Button)nav.Controls[4]).PerformClick();
                check("settings shortcut retains page and invokes existing action once",tabs.SelectedIndex==1 && invoked==1,null);
                host.ClientSize=new Size(680,500);
                check("settings compact window uses top navigation",nav.RowCount==1 && nav.ColumnCount==5,null);
            }
        }
        private static void VerifyCompactSettings(Action<string, bool, string> check)
        {
            foreach (float scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F })
            using (var form = new MainForm { OfflinePreview = true })
            {
                var settings = (Form)Field(form, "settingsForm");
                CreateHandles(settings);
                settings.Scale(new SizeF(scale, scale));
                settings.Font = new Font(settings.Font.FontFamily, 9F * scale);
                settings.ClientSize = new Size((int)(794 * scale), (int)(700 * scale));
                settings.Opacity = 0; settings.Show();
                var tabs = (SettingsTabs)((DarkSettingsForm)settings).ContentHost.Controls[0];
                var page = (Panel)Field(form, "settingsGeneralPage");
                var list = (CheckedListBox)Field(form, "autoConnectDeviceList");
                var grid = (TableLayoutPanel)Field(form, "settingsGeneralGrid");
                foreach (int count in new[] { 0, 1, 2, 4, 8 })
                {
                    var receivers = new List<Receiver>();
                    for (int i = 0; i < count; i++) receivers.Add(Speaker("音响 " + (i + 1), i + 1));
                    Populate(form, receivers);
                    Call(form, "RefreshAutoConnectDevices");
                    tabs.SelectedIndex = 1; settings.PerformLayout();
                    Application.DoEvents();
                    string detail = "";
                    check("compact general fits " + count + " devices at " + scale, Fits(settings, ref detail), detail);
                    check("compact general has no page scrollbar " + count + " at " + scale,
                        !page.VerticalScroll.Visible && !page.HorizontalScroll.Visible,
                        "grid=" + grid.Height + "; viewport=" + page.ClientSize);
                    check("compact list preserves all targets " + count + " at " + scale, list.Items.Count == count, null);
                    if (count == 8)
                    {
                        list.SetItemChecked(7, true); list.SetItemChecked(0, true);
                        int height = grid.Height;
                        Call(form, "RefreshAutoConnectDevices");
                        check("compact relayout preserves selected IDs/order " + scale,
                            list.CheckedItems.Count == 2 && list.Items[0].ToString().StartsWith("音响 8") &&
                            list.Items[1].ToString().StartsWith("音响 1") && grid.Height == height, null);
                    }
                    if (scale == 1F && count == 2)
                    { RenderPreview(settings, "compact-general-two-devices"); settings.Show(); }
                }
                tabs.SelectedIndex = 0; settings.PerformLayout();
                var pattern = (Button)Field(form, "patternButton");
                var eq = (Button)Field(form, "calibrationButton");
                var options = (TableLayoutPanel)Field(form, "settingsOptions");
                var mute = options.GetControlFromPosition(0, 3);
                var row = pattern.Parent;
                Point first = options.PointToClient(pattern.PointToScreen(Point.Empty));
                Point last = options.PointToClient(eq.PointToScreen(new Point(eq.Width, 0)));
                check("compact action buttons align with cards " + scale,
                    first.X == mute.Left && last.X == mute.Right && pattern.Top == eq.Top &&
                    pattern.Height == eq.Height && Math.Abs(pattern.Width - eq.Width) <= 1 &&
                    mute.Top - (row.Top + pattern.Bottom) >= 8,
                    "first=" + first + "; last=" + last + "; mute=" + mute.Bounds);
                if (scale == 1F) RenderPreview(settings, "compact-audio-actions");
                settings.Show(); tabs.SelectedIndex = 1;
                settings.ClientSize = new Size(680, 440);
                settings.PerformLayout(); Application.DoEvents();
                string smallDetail = "";
                check("compact small window retains scroll access " + scale,
                    page.AutoScroll && page.VerticalScroll.Visible && Fits(settings, ref smallDetail), smallDetail);
            }
        }
    }
}
