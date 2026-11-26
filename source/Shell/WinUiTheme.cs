using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace AirStereo2;

/// <summary>
/// Presentation-only theme preference matching AirStereo2 while preserving
/// AirStereo2's existing interface.json persistence key ("theme").
/// </summary>
internal static class WinUiTheme
{
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
            // Resolve Windows' current preference explicitly so switching back
            // to System never inherits a previously fixed Application.RequestedTheme.
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
        UiPreferences.Set("theme", (int)mode);
        Mode = mode;
        Changed?.Invoke(mode);
    }

    private static WinUiAppearanceMode Read()
    {
        int value = UiPreferences.Read("theme", 0);
        return Enum.IsDefined(typeof(WinUiAppearanceMode), value)
            ? (WinUiAppearanceMode)value
            : WinUiAppearanceMode.System;
    }
}

internal enum WinUiAppearanceMode
{
    System = 0,
    Light = 1,
    Dark = 2
}
