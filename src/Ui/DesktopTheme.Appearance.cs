using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirStereo.Ui
{
    internal enum AppearanceMode { System, Light, Dark }

    // Presentation only. Never shares or rewrites the playback settings file.
    internal static class AppearanceSettings
    {
        internal static AppearanceMode Read(string path)
        {
            try
            {
                return Enum.TryParse(File.ReadAllText(path).Trim(), out AppearanceMode mode) &&
                    Enum.IsDefined(typeof(AppearanceMode), mode) ? mode : AppearanceMode.System;
            }
            catch (IOException) { return AppearanceMode.System; }
            catch (UnauthorizedAccessException) { return AppearanceMode.System; }
        }

        internal static void Write(string path, AppearanceMode mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, mode.ToString());
            File.Move(temporary, path, true);
        }
    }

    internal static partial class DesktopTheme
    {
        internal enum Role { Canvas, Surface, Ink, Muted, Accent, StrongAccent, Selection, Border, Log, LogInk, Disabled, OnAccent }
        internal sealed class Palette
        {
            internal readonly Color[] Colors;
            internal readonly bool Dark, HighContrast;
            internal Palette(bool dark, bool highContrast)
            {
                Dark = dark; HighContrast = highContrast;
                Colors = highContrast ? new[] {
                    SystemColors.Window, SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
                    SystemColors.HotTrack, SystemColors.Highlight, SystemColors.Highlight,
                    SystemColors.WindowText, SystemColors.Window, SystemColors.WindowText,
                    SystemColors.GrayText, SystemColors.HighlightText
                } : dark ? new[] {
                    Color.FromArgb(24,24,27), Color.FromArgb(34,34,38), Color.FromArgb(244,244,245), Color.FromArgb(173,173,184),
                    Color.FromArgb(145,193,255), Color.FromArgb(112,174,250), Color.FromArgb(39,57,78),
                    Color.FromArgb(62,62,69), Color.FromArgb(28,28,32), Color.FromArgb(212,216,224),
                    Color.FromArgb(120,120,130), Color.FromArgb(15,28,45)
                } : new[] {
                    Color.FromArgb(243,243,247), Color.FromArgb(253,253,255), Color.FromArgb(27,27,31), Color.FromArgb(94,94,106),
                    Color.FromArgb(0,89,163), Color.FromArgb(0,95,184), Color.FromArgb(222,237,253),
                    Color.FromArgb(211,211,220), Color.FromArgb(247,247,251), Color.FromArgb(53,57,66),
                    Color.FromArgb(125,125,136), Color.White
                };
            }
            internal Color this[Role role] => Colors[(int)role];
        }

        // Tests construct UI without reading the user's preference. The actual app
        // loads System (or its saved override) during OnLoad, before discovery.
        internal static AppearanceMode Mode { get; private set; } = AppearanceMode.Dark;
        internal static Palette Current { get; private set; } = new Palette(true, false);
        internal static event Action<Palette> Changed;
        internal static Color Canvas => Current[Role.Canvas];
        internal static Color Surface => Current[Role.Surface];
        internal static Color Ink => Current[Role.Ink];
        internal static Color Muted => Current[Role.Muted];
        internal static Color Accent => Current[Role.Accent];
        internal static Color StrongAccent => Current[Role.StrongAccent];
        internal static Color Selection => Current[Role.Selection];
        internal static Color Border => Current[Role.Border];
        internal static Color Log => Current[Role.Log];
        internal static Color LogInk => Current[Role.LogInk];
        internal static Color Disabled => Current[Role.Disabled];
        internal static Color OnAccent => Current[Role.OnAccent];

        internal static bool ResolveDark(AppearanceMode mode, bool systemLight) =>
            mode == AppearanceMode.Dark || mode == AppearanceMode.System && !systemLight;

        internal static void SetMode(AppearanceMode mode) => SetMode(mode, SystemUsesLight(), SystemInformation.HighContrast);
        internal static void SetMode(AppearanceMode mode, bool systemLight, bool highContrast)
        {
            if (!Enum.IsDefined(typeof(AppearanceMode), mode)) mode = AppearanceMode.System;
            bool dark = ResolveDark(mode, systemLight);
            if (Mode == mode && Current.Dark == dark && Current.HighContrast == highContrast) return;
            Palette previous = Current;
            Mode = mode;
            Current = new Palette(dark, highContrast);
            Changed?.Invoke(previous);
        }

        private static bool SystemUsesLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return (key?.GetValue("AppsUseLightTheme") as int? ?? 1) != 0;
            }
            catch (System.Security.SecurityException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal static void WindowChrome(Form form)
        {
            if (!form.IsHandleCreated || !form.Visible) return;
            try
            {
                int round = Current.HighContrast ? 1 : 2;
                int dark = Current.Dark && !Current.HighContrast ? 1 : 0;
                // Unsupported attributes fail harmlessly on Windows 10.
                DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        internal static void Attach(Form form) => new WindowTheme(form);

        // Only windows subscribe to the theme event. Child bindings are weak, so
        // repeated discovery can dispose/recreate rows without retaining them.
        private sealed class WindowTheme
        {
            private readonly Form form;
            private readonly ConditionalWeakTable<Control, Binding> bindings = new ConditionalWeakTable<Control, Binding>();
            internal WindowTheme(Form form)
            {
                this.form = form;
                Changed += Apply;
                form.HandleCreated += HandleCreated;
                form.Shown += Shown;
                form.Disposed += Disposed;
            }
            // Do not force a recursive layout/native-theme pass from HandleCreated:
            // child handles may still be under construction (including EDIT hooks).
            private void HandleCreated(object sender, EventArgs args) => WindowChrome(form);
            private void Shown(object sender, EventArgs args) => Apply(Current);
            private void Disposed(object sender, EventArgs args)
            {
                Changed -= Apply;
                form.HandleCreated -= HandleCreated;
                form.Shown -= Shown;
                form.Disposed -= Disposed;
            }
            private void Apply(Palette previous)
            {
                if (form.IsDisposed) return;
                var entries = new List<KeyValuePair<Control, Binding>>();
                Capture(form, previous, entries);
                form.SuspendLayout();
                try
                {
                    foreach (var entry in entries) entry.Value.Apply(entry.Key);
                    foreach (var entry in entries)
                        if (entry.Key is SettingsTabs tabs) tabs.RefreshTheme();
                    WindowChrome(form);
                }
                finally { form.ResumeLayout(false); }
                form.Invalidate(true);
            }
            private void Capture(Control control, Palette previous, List<KeyValuePair<Control, Binding>> entries)
            {
                Binding binding = bindings.GetValue(control, _ => new Binding());
                binding.Capture(control, previous);
                entries.Add(new KeyValuePair<Control, Binding>(control, binding));
                foreach (Control child in control.Controls) Capture(child, previous, entries);
            }
        }

        private sealed class Binding
        {
            private Role? back, fore;
            private Color lastBack, lastFore;
            private bool initialized;
            internal void Capture(Control control, Palette previous)
            {
                if (!initialized || control.BackColor != lastBack) back = Match(control.BackColor, previous, false);
                if (!initialized || control.ForeColor != lastFore) fore = Match(control.ForeColor, previous, true);
                initialized = true;
            }
            private static Role? Match(Color value, Palette palette, bool foreground)
            {
                Role[] roles = foreground ? new[] { Role.Ink, Role.Muted, Role.Accent, Role.StrongAccent, Role.LogInk, Role.Disabled, Role.OnAccent } :
                    new[] { Role.Canvas, Role.Surface, Role.Selection, Role.Log, Role.Border };
                foreach (Role role in roles) if (value.ToArgb() == palette[role].ToArgb()) return role;
                if (value == SystemColors.Control) return Role.Canvas;
                if (value == SystemColors.ControlText || value == SystemColors.WindowText) return Role.Ink;
                return null;
            }
            internal void Apply(Control control)
            {
                if (back.HasValue) control.BackColor = Current[back.Value];
                if (fore.HasValue) control.ForeColor = Current[fore.Value];
                if (Current.HighContrast && back == Role.Selection) control.ForeColor = SystemColors.HighlightText;
                lastBack = control.BackColor; lastFore = control.ForeColor;
                if (control is Button button)
                {
                    button.FlatAppearance.MouseOverBackColor = Selection;
                    button.FlatAppearance.MouseDownBackColor = Border;
                    button.FlatAppearance.BorderColor = Border;
                }
                if (control is TextBoxBase || control is ListBox || control is ComboBox)
                    SetNativeTheme(control);
            }
        }

        internal static void SetNativeTheme(Control control)
        {
            if (!control.IsHandleCreated) return;
            try { SetWindowTheme(control.Handle, Current.Dark && !Current.HighContrast ? "DarkMode_Explorer" : "Explorer", null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        internal sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color MenuItemSelected => Selection;
            public override Color MenuItemBorder => Border;
            public override Color MenuBorder => Border;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Surface;
        }
    }
}
