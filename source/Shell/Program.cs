using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace AirStereo2;

internal static class Program
{
    private const string InstanceMutexName = "Local\\AirStereo2.WinUI.1";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0].Equals("--fix-firewall", StringComparison.OrdinalIgnoreCase)) {
            return AirStereo2.Services.FirewallFix.RunElevatedApply(args[1], args[2]);
        }
        bool startupLaunch = Array.Exists(args, value => value.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, InstanceMutexName, out bool created);
        if (!created) {
            if (!startupLaunch) ActivateExistingWindow();
            return 0;
        }
        try {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters => {
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new FluentApplication(startupLaunch);
            });
            return 0;
        } catch (Exception ex) {
            UiPreferences.Record(ex.ToString());
            WinUiNative.MessageBoxW(0, "AirStereo2 界面启动失败：" + ex.Message, "AirStereo2", 0x10);
            return 1;
        }
    }

    private static void ActivateExistingWindow()
    {
        for (int attempt = 0; attempt < 20; attempt++) {
            nint host = FindWindow(null, "AirStereo2 tray host");
            if (host != 0 && WinUiNative.PostMessageW(host, WinUiNative.OpenPopupMessage, 0, 0))
                return;
            Thread.Sleep(50);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string? cls, string title);
}

public sealed partial class FluentApplication : Application
{
    private readonly bool startupLaunch;
    ShellWindow? window;

    public FluentApplication() : this(false) { }

    internal FluentApplication(bool startInTray)
    {
        startupLaunch = startInTray;
        UnhandledException += (_, e) => UiPreferences.Record(e.Exception.ToString());
        // Apply persisted dark/light theme before InitializeComponent() so the
        // first XAML surface starts in the saved theme without a light flash.
        if (WinUiTheme.Mode == WinUiAppearanceMode.Dark)
            RequestedTheme = ApplicationTheme.Dark;
        else if (WinUiTheme.Mode == WinUiAppearanceMode.Light)
            RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new ShellWindow();
        window.InitializeLaunch(startupLaunch);
    }
}

internal static class UiPreferences
{
    internal static string Directory { get; } = ResolveDirectory();
    static string ResolveDirectory()
    {
        if (WinUiNative.IsPackaged()) {
            string packagedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo2", "Interface");
            try { System.IO.Directory.CreateDirectory(packagedDir); } catch { }
            return packagedDir;
        }
        string local = Path.Combine(AppContext.BaseDirectory, "Data", "Interface");
        try {
            System.IO.Directory.CreateDirectory(local);
            string probe = Path.Combine(local, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        } catch {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirStereo2", "Interface");
            try { System.IO.Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }
    }
    static readonly Dictionary<string, JsonElement> values = Load();
    static Dictionary<string, JsonElement> Load() {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Directory, "interface.json"))) ?? new(); } catch { return new(); }
    }
    internal static event Action<string>? Changed;
    internal static T Read<T>(string name, T fallback) { try { return values.TryGetValue(name, out var v) ? v.Deserialize<T>()! : fallback; } catch { return fallback; } }
    internal static void Set<T>(string name, T value) {
        var next = JsonSerializer.SerializeToElement(value);
        if (values.TryGetValue(name, out var current) && current.GetRawText() == next.GetRawText()) return;
        values[name] = next;
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, "interface.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(values));
        File.Move(path + ".tmp", path, true);
        Changed?.Invoke(name);
    }
    internal static void Record(string message) { try { System.IO.Directory.CreateDirectory(Directory); File.AppendAllText(Path.Combine(Directory, "interface.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { } }
}
