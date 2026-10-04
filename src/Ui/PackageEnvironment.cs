using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AirStereo.Ui
{
    /// <summary>Detect actual Windows package identity, not the executable's folder name.</summary>
    internal static class PackageEnvironment
    {
        internal static readonly string FamilyName = ReadFamilyName();
        internal static bool IsPackaged => FamilyName != null;
        internal const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";
        private static readonly Lazy<string> dataDirectory = new Lazy<string>(ReadDataDirectory);
        internal static string DataDirectory => dataDirectory.Value;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder name);

        [DllImport("AirStereo.Store.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern int AirStereoDataDirectory(StringBuilder path, uint capacity);

        private static string ReadFamilyName()
        {
            if (!OperatingSystem.IsWindows()) return null;
            uint length = 0;
            int result = GetCurrentPackageFamilyName(ref length, null);
            if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
            if (result != 122) throw new Win32Exception(result);
            StringBuilder name = new StringBuilder(checked((int)length));
            result = GetCurrentPackageFamilyName(ref length, name);
            if (result != 0) throw new Win32Exception(result);
            return name.ToString();
        }

        private static string ReadDataDirectory()
        {
            if (!IsPackaged)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo");
            StringBuilder path = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(AirStereoDataDirectory(path, (uint)path.Capacity));
            return path.ToString();
        }

        internal static string DiagnosticDirectory(string installDirectory, string userDataDirectory, bool packaged)
        {
            return Path.Combine(packaged ? userDataDirectory : installDirectory, "Diagnostics");
        }
    }
}
