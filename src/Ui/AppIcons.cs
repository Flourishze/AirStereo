using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirStereo.Ui
{
    /// <summary>Embedded, transparent icon assets. No GetHicon/borrowed native handles.</summary>
    internal static class AppIcons
    {
        internal static Icon Load(string name, int size)
        {
            using (Stream stream = typeof(AppIcons).Assembly.GetManifestResourceStream("AirStereo.Icons." + name + ".ico"))
            {
                if (stream == null) throw new InvalidOperationException("Missing application icon: " + name);
                using (Icon source = new Icon(stream, size, size)) return (Icon)source.Clone();
            }
        }

        // Taskbar colour is controlled by SystemUsesLightTheme, independently of
        // AppsUseLightTheme and AirStereo's manual appearance override.
        internal static bool LightTaskbar()
        {
            if (SystemInformation.HighContrast) return SystemColors.Window.GetBrightness() > .5F;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return (key?.GetValue("SystemUsesLightTheme") as int? ?? 0) != 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
    }

    public sealed partial class MainForm
    {
        private Icon notificationIcon;
        private bool notificationOnLight;
        private int notificationSize;

        private void UpdateNotificationIcon()
        {
            if (trayIcon == null || IsDisposed) return;
            bool light = AppIcons.LightTaskbar();
            int size = Math.Max(16, Math.Max(SystemInformation.SmallIconSize.Width,
                (int)Math.Round(16 * Math.Max(96, DeviceDpi) / 96.0)));
            if (notificationIcon != null && notificationOnLight == light && notificationSize == size) return;
            Icon next = AppIcons.Load(light ? "tray-dark" : "tray-light", size);
            Icon previous = notificationIcon;
            trayIcon.Icon = next;
            notificationIcon = next;
            notificationOnLight = light;
            notificationSize = size;
            previous?.Dispose();
        }
    }
}
