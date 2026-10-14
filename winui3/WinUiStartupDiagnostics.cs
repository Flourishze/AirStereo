using System.IO;

namespace AirStereo;

/// <summary>Startup diagnostics must never make a failed log write an application failure.</summary>
internal static class WinUiStartupDiagnostics
{
    internal static void Trace(string stage) => Write("winui-startup-trace.txt", stage);

    internal static void Error(Exception? error, string? message = null)
    {
        try { Write("winui-startup-error.txt", error?.ToString() ?? message ?? "Unknown startup error."); }
        catch { } // Even exception formatting must not replace the original failure.
    }

    private static void Write(string fileName, string text)
    {
        try
        {
            string entry = $"{DateTimeOffset.Now:O} [PID {Environment.ProcessId}] {text}{Environment.NewLine}";
            WriteToDirectories(fileName, entry,
                () => Path.Combine(Ui.PackageEnvironment.DataDirectory, "Diagnostics"),
                () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo", "Diagnostics"),
                () => Path.Combine(Path.GetTempPath(), "AirStereo", "Diagnostics"));
        }
        catch { } // Diagnostics are best effort; application exceptions still propagate.
    }

    internal static bool WriteToDirectories(string fileName, string text, params Func<string>[] directories)
    {
        foreach (Func<string> resolve in directories)
        {
            try
            {
                string directory = resolve();
                if (string.IsNullOrWhiteSpace(directory)) continue;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, fileName), text);
                return true;
            }
            catch { } // Includes failed package-data initialization and denied/locked files.
        }
        return false;
    }
}
