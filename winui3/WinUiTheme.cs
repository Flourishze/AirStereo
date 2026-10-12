using System.IO;
using Microsoft.UI.Xaml;
using AirStereo.Ui;
using Windows.UI.ViewManagement;

namespace AirStereo;

/// <summary>
/// Presentation-only theme preference for the WinUI surface.  It deliberately
/// uses the same appearance.txt location as the desktop surface without
/// coupling the WinUI project to the WinForms theme implementation.
/// </summary>
internal static class WinUiTheme
{
    private static readonly string preferencePath = Path.Combine(PackageEnvironment.DataDirectory, "appearance.txt");
    private static readonly UISettings systemAppearance = new();

    internal static WinUiAppearanceMode Mode { get; private set; } = Read();
    internal static event Action<WinUiAppearanceMode>? Changed;

    static WinUiTheme()
    {
        systemAppearance.ColorValuesChanged += (_, _) =>
        {
            if (Mode == WinUiAppearanceMode.System) Changed?.Invoke(Mode);
        };
    }

    internal static void Apply(FrameworkElement root)
    {
        root.RequestedTheme = Mode switch
        {
            WinUiAppearanceMode.Light => ElementTheme.Light,
            WinUiAppearanceMode.Dark => ElementTheme.Dark,
            // Application.RequestedTheme is fixed at startup. Resolve Windows'
            // current preference explicitly so switching back to System never
            // inherits a previously saved dark/light application default.
            _ => SystemTheme()
        };
    }

    private static ElementTheme SystemTheme()
    {
        var background = systemAppearance.GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B < 384 ? ElementTheme.Dark : ElementTheme.Light;
    }

    internal static bool IsDark(FrameworkElement root) => Mode switch
    {
        WinUiAppearanceMode.Dark => true,
        WinUiAppearanceMode.Light => false,
        _ => root.ActualTheme == ElementTheme.Dark
    };

    internal static void Set(WinUiAppearanceMode mode)
    {
        if (!Enum.IsDefined(mode)) mode = WinUiAppearanceMode.System;
        if (mode == Mode) return;
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        string temporary = preferencePath + ".tmp";
        File.WriteAllText(temporary, mode.ToString());
        File.Move(temporary, preferencePath, true);
        Mode = mode;
        Changed?.Invoke(mode);
    }

    private static WinUiAppearanceMode Read()
    {
        try
        {
            string value = File.ReadAllText(preferencePath).Trim();
            return Enum.TryParse(value, true, out WinUiAppearanceMode mode) && Enum.IsDefined(mode)
                ? mode : WinUiAppearanceMode.System;
        }
        catch (IOException) { return WinUiAppearanceMode.System; }
        catch (UnauthorizedAccessException) { return WinUiAppearanceMode.System; }
    }
}

internal enum WinUiAppearanceMode
{
    System,
    Light,
    Dark
}
