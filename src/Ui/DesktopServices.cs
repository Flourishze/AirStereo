using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirStereo.Ui
{
    internal static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        [DllImport("AirStereo.Store.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern int AirStereoStartupState(out int state);
        [DllImport("AirStereo.Store.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern int AirStereoSetStartup(int enabled, out int state);
        internal static string Command(string directory, string host)
        {
            string exe = Path.Combine(directory, "AirStereo.exe");
            if (File.Exists(exe)) return "\"" + exe + "\" gui --tray";
            return "\"" + host + "\" \"" + Path.Combine(directory, "AirStereo.dll") + "\" gui --tray";
        }

        public static bool Enabled
        {
            get
            {
                if (PackageEnvironment.IsPackaged)
                {
                    Marshal.ThrowExceptionForHR(AirStereoStartupState(out int state));
                    return state == 3 || state == 4; // Enabled / EnabledByPolicy
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue("AirStereo") is string command && command.Length > 0;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (PackageEnvironment.IsPackaged)
            {
                Marshal.ThrowExceptionForHR(AirStereoSetStartup(enabled ? 1 : 0, out int state));
                if (enabled && state != 3 && state != 4)
                    throw new InvalidOperationException(state == 1 ?
                        "启动项已在 Windows 中关闭，请到“设置 → 应用 → 启动”手动启用 AirStereo。" :
                        "Windows 策略禁止启用启动项。");
                if (!enabled && (state == 3 || state == 4))
                    throw new InvalidOperationException("Windows 策略不允许关闭此启动项。");
                return;
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue("AirStereo", Command(AppContext.BaseDirectory, Environment.ProcessPath), RegistryValueKind.String);
                else key.DeleteValue("AirStereo", false);
            }
        }
    }

    internal sealed class FaultEntry
    {
        public DateTimeOffset Time { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public string Detail { get; set; }
        public string Context { get; set; }
        public string Runtime { get; set; }
        public string[] RecentActivity { get; set; }
    }

    /// <summary>Local, bounded diagnostics. Never records pairing secrets or raw audio.</summary>
    internal sealed class FaultStore
    {
        private readonly object gate = new object();
        private readonly Queue<string> activity = new Queue<string>();
        private readonly string preferredDirectory;
        private readonly string fallbackDirectory;
        private const long MaxBytes = 5 * 1024 * 1024;
        public string DirectoryPath { get; private set; }
        public string StorageError { get; private set; } = "";
        public bool IsFallback => !string.Equals(DirectoryPath, preferredDirectory, StringComparison.OrdinalIgnoreCase);
        public static FaultStore Default { get; } = new FaultStore(
            PackageEnvironment.DiagnosticDirectory(AppContext.BaseDirectory, PackageEnvironment.DataDirectory, PackageEnvironment.IsPackaged),
            Path.Combine(PackageEnvironment.DataDirectory, "Diagnostics"));

        internal FaultStore(string preferred, string fallback)
        {
            preferredDirectory = preferred;
            fallbackDirectory = fallback;
            DirectoryPath = preferred;
        }

        public void Activity(string line)
        {
            lock (gate)
            {
                activity.Enqueue(line);
                while (activity.Count > 240) activity.Dequeue();
            }
        }

        public void Record(string category, string message, string detail, string context)
        {
            lock (gate)
            {
                FaultEntry entry = new FaultEntry { Time = DateTimeOffset.Now,
                    Category = category, Message = message, Detail = detail ?? "", Context = context ?? "",
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    RecentActivity = activity.ToArray() };
                string json = JsonSerializer.Serialize(entry) + Environment.NewLine;
                try { Append(json); StorageError = ""; }
                catch (Exception primary)
                {
                    DirectoryPath = fallbackDirectory;
                    try { Append(json); StorageError = ""; }
                    catch (Exception fallback) { StorageError = primary.Message + " / " + fallback.Message; }
                }
            }
        }

        private void Append(string json)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "faults.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Copy(path, Path.Combine(DirectoryPath, "faults.previous.jsonl"), true);
                File.WriteAllText(path, "", new UTF8Encoding(false));
            }
            File.AppendAllText(path, json, new UTF8Encoding(false));
        }

        public List<FaultEntry> Read()
        {
            lock (gate)
            {
                List<FaultEntry> entries = new List<FaultEntry>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in new[] { preferredDirectory, fallbackDirectory })
                {
                    if (!seen.Add(directory)) continue;
                    foreach (string name in new[] { "faults.previous.jsonl", "faults.jsonl" })
                    {
                        string path = Path.Combine(directory, name);
                        try
                        {
                            if (!File.Exists(path)) continue;
                            if (string.Equals(directory, fallbackDirectory, StringComparison.OrdinalIgnoreCase))
                                DirectoryPath = directory;
                            foreach (string line in File.ReadLines(path))
                            {
                                try { FaultEntry entry = JsonSerializer.Deserialize<FaultEntry>(line); if (entry != null) entries.Add(entry); }
                                catch (JsonException) { }
                            }
                        }
                        catch (Exception error) { StorageError = error.Message; }
                    }
                }
                entries.Sort((left, right) => right.Time.CompareTo(left.Time));
                if (entries.Count > 500) entries.RemoveRange(500, entries.Count - 500);
                return entries;
            }
        }

        public string EnsureDirectory()
        {
            lock (gate)
            {
                try { Directory.CreateDirectory(DirectoryPath); }
                catch
                {
                    // No faults yet, or the install directory is read-only: create the fallback.
                    Directory.CreateDirectory(fallbackDirectory);
                    DirectoryPath = fallbackDirectory;
                }
                return DirectoryPath;
            }
        }

        public void Export(string path, string context)
        {
            lock (gate)
            {
                object report = new { Application = "AirStereo", GeneratedAt = DateTimeOffset.Now,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    InstallDirectory = AppContext.BaseDirectory, DiagnosticsDirectory = DirectoryPath,
                    StorageError, Context = context, Faults = Read(), RecentActivity = activity.ToArray() };
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }
    }
}


