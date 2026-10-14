using System.Runtime.InteropServices;
using System.IO;
using Microsoft.UI.Xaml;

namespace AirStereo;

/// <summary>
/// Small Win32 boundary used only for the tray shell and popup placement.  The
/// AirPlay/audio implementation remains in the 1.0.6 source tree.
/// </summary>
internal static class WinUiNative
{
    internal const uint TrayCallback = 0x8001;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonUp = 0x0205;
    internal static readonly uint OpenPopupMessage = RegisterWindowMessageW("AirStereo.WinUI.OpenPopup.1");

    internal delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClass
    {
        public uint style;
        public WndProc proc;
        public int classExtra, windowExtra;
        public nint instance, icon, cursor, background;
        public string? menu;
        public string name;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint ID, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct IconIdentifier { public uint Size; public nint Window; public uint ID; public Guid Guid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadImageW(nint instance, string name, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIconW(uint action, ref NotifyIconData data);
    [DllImport("shell32.dll")] internal static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flag);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int value, uint size);


    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsPopup = unchecked((long)0x80000000u);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint DwmwaNonClientRenderingPolicy = 2;
    private const uint DwmwaBorderColor = 34;
    private const int DwmNcrpDisabled = 1;
    private const int DwmColorNone = -2;

    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 and DWMWA_BORDER_COLOR = 34.
    // The tray surface has no title bar, so leaving the DWM defaults enabled
    // can produce a bright one-pixel frame around a dark WinUI surface.
    internal static void ApplyWindowTheme(nint hwnd, bool dark)
    {
        if (hwnd == 0) return;
        int immersiveDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref immersiveDark, sizeof(int));
        // COLOR_NONE is important here.  A border color that merely matches
        // the content still leaves the Windows 11 visible-frame strip, which
        // is especially obvious as a white line around a dark tray popup.
        int border = DwmColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        // Windows 11 uses separate caption attributes for the normal title
        // bar.  Setting them as well as AppWindow.TitleBar makes the setting
        // window follow the app theme even when the system title-bar visuals
        // have already been cached by DWM.
        int caption = ColorRef(dark ? 31 : 243, dark ? 31 : 243, dark ? 31 : 247);
        int text = dark ? ColorRef(255, 255, 255) : ColorRef(0, 0, 0);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
    }

    internal static void RemoveNonClientFrame(nint hwnd)
    {
        if (hwnd == 0) return;
        long style = GetWindowLongPtrW(hwnd, GwlStyle).ToInt64();
        long cleaned = (style & ~(WsCaption | WsThickFrame | WsBorder | WsDlgFrame)) | WsPopup;
        if (cleaned != style)
            SetWindowLongPtrW(hwnd, GwlStyle, (nint)cleaned);
        int renderingPolicy = DwmNcrpDisabled;
        DwmSetWindowAttribute(hwnd, DwmwaNonClientRenderingPolicy, ref renderingPolicy, sizeof(int));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
    }

    private static int ColorRef(int red, int green, int blue) =>
        (blue << 16) | (green << 8) | red;
    [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(nint parent, string text, string title, uint flags);
}

internal sealed class WinUiTrayIcon : IDisposable
{
    private readonly WinUiNative.WndProc proc;
    private readonly nint hwnd;
    private nint icon;
    private readonly uint taskbarCreated;
    private WinUiNative.NotifyIconData data;
    private bool disposed;

    internal event Action? Toggle;
    internal event Action? Open;
    internal event Action? Settings;
    internal event Action? StopPlayback;
    internal event Action? Exit;
    internal bool MenuOpen { get; private set; }
    internal bool Registered { get; private set; }

    internal WinUiTrayIcon(string iconPath)
    {
        proc = WindowProc;
        var wc = new WinUiNative.WndClass
        {
            proc = proc,
            instance = WinUiNative.GetModuleHandleW(null),
            name = "AirStereo.WinUI.Tray." + Guid.NewGuid().ToString("N")
        };
        if (WinUiNative.RegisterClassW(ref wc) == 0)
            throw new InvalidOperationException("托盘窗口注册失败");
        hwnd = WinUiNative.CreateWindowExW(0x80, wc.name, "AirStereo tray host", 0,
            0, 0, 0, 0, 0, 0, wc.instance, 0);
        icon = WinUiNative.LoadImageW(0, iconPath, 1, 32, 32, 0x10);
        if (hwnd == 0 || icon == 0)
            throw new InvalidOperationException("托盘资源创建失败");
        taskbarCreated = WinUiNative.RegisterWindowMessageW("TaskbarCreated");
        data = new WinUiNative.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<WinUiNative.NotifyIconData>(),
            Window = hwnd, ID = 1, Flags = 7, Callback = WinUiNative.TrayCallback,
            Icon = icon, Tip = WinUiLanguage.T("AirStereo · 立体声 AirPlay 发送器"), Info = "", InfoTitle = ""
        };
        Add();
    }

    private void Add() => Registered = WinUiNative.Shell_NotifyIconW(0, ref data);

    internal void SetTip(string text)
    {
        if (disposed) return;
        data.Tip = text.Length > 127 ? text.Substring(0, 127) : text;
        WinUiNative.Shell_NotifyIconW(1, ref data);
    }

    internal (WinUiNative.Rect Work, double Scale) WorkArea()
    {
        var id = new WinUiNative.IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<WinUiNative.IconIdentifier>(), Window = hwnd, ID = 1
        };
        WinUiNative.GetCursorPos(out var point);
        if (WinUiNative.Shell_NotifyIconGetRect(ref id, out var rect) == 0)
            point = new WinUiNative.Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        nint monitor = WinUiNative.MonitorFromPoint(point, 2);
        var info = new WinUiNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WinUiNative.MonitorInfo>() };
        if (!WinUiNative.GetMonitorInfoW(monitor, ref info))
            throw new InvalidOperationException("无法读取显示器工作区");
        WinUiNative.GetDpiForMonitor(monitor, 0, out uint dpi, out _);
        return (info.Work, Math.Max(1.0, dpi / 96.0));
    }

    private nint WindowProc(nint h, uint message, nint wParam, nint lParam)
    {
        // Restart Manager / WiX use the standard end-session messages. Exit
        // through the existing path so playback, mute recovery and settings
        // windows are cleaned up instead of merely hiding the tray popup.
        if (message == 0x0011) return 1; // WM_QUERYENDSESSION
        if (message == 0x0016 && wParam != 0) { Exit?.Invoke(); return 0; } // WM_ENDSESSION
        if (message == WinUiNative.OpenPopupMessage && message != 0) { Open?.Invoke(); return 0; }
        if (message == taskbarCreated && taskbarCreated != 0) Add();
        if (message != WinUiNative.TrayCallback) return WinUiNative.DefWindowProcW(h, message, wParam, lParam);
        if ((uint)lParam == WinUiNative.WmLButtonUp) { Toggle?.Invoke(); return 0; }
        if ((uint)lParam != WinUiNative.WmRButtonUp) return 0;

        MenuOpen = true;
        WinUiNative.GetCursorPos(out var p);
        nint menu = WinUiNative.CreatePopupMenu();
        try
        {
            WinUiNative.AppendMenuW(menu, 0, 1, WinUiLanguage.T("打开 AirStereo"));
            WinUiNative.AppendMenuW(menu, 0, 2, WinUiLanguage.T("音频设置"));
            WinUiNative.AppendMenuW(menu, 0, 3, WinUiLanguage.T("停止播放"));
            WinUiNative.AppendMenuW(menu, 0x800, 0, "");
            WinUiNative.AppendMenuW(menu, 0, 4, WinUiLanguage.T("退出"));
            WinUiNative.SetForegroundWindow(hwnd);
            uint command = WinUiNative.TrackPopupMenu(menu, 0x100 | 0x2, p.X, p.Y, 0, hwnd, 0);
            HandleMenuCommand(command);
        }
        finally { WinUiNative.DestroyMenu(menu); MenuOpen = false; }
        return 0;
    }

    internal void HandleMenuCommand(uint command)
    {
        if (command == 1) Toggle?.Invoke();
        else if (command == 2) Settings?.Invoke();
        else if (command == 3) StopPlayback?.Invoke();
        else if (command == 4) Exit?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        WinUiNative.Shell_NotifyIconW(2, ref data);
        WinUiNative.DestroyWindow(hwnd);
        if (icon != 0) WinUiNative.DestroyIcon(icon);
        icon = 0;
    }
}

internal static class WinUiLauncher
{
    private static WinUiApplication? application;

    internal static int Run(bool startInTray = false,
        int silenceDisconnectMilliseconds = Session.SessionOptions.DefaultSourceSilenceDisconnectMilliseconds,
        bool useAlac = true, Session.SilenceFrameMode silenceMode = Session.SilenceFrameMode.RepeatLast)
    {
        try
        {
            WinUiStartupDiagnostics.Trace("Application.Start");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(queueController =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                application = new WinUiApplication(startInTray, silenceDisconnectMilliseconds, useAlac, silenceMode);
            });
            return 0;
        }
        catch (Exception error)
        {
            WinUiStartupDiagnostics.Error(error);
            WinUiNative.MessageBoxW(0, "AirStereo WinUI 3 启动失败：" + error.Message, "AirStereo", 0x10);
            return 1;
        }
    }
}

public sealed partial class WinUiApplication : Microsoft.UI.Xaml.Application
{
    private readonly bool startInTray;
    private readonly int silenceDisconnectMilliseconds;
    private readonly bool useAlac;
    private readonly Session.SilenceFrameMode silenceMode;
    private WinUiMainWindow? main;
    private readonly bool languageSelfTest;
    private readonly bool memorySelfTest;

    internal WinUiApplication(bool startInTray, int silenceDisconnectMilliseconds, bool useAlac,
        Session.SilenceFrameMode silenceMode, bool languageSelfTest = false, bool memorySelfTest = false)
    {
        UnhandledException += (_, args) => WinUiStartupDiagnostics.Error(args.Exception, args.Message);
        WinUiStartupDiagnostics.Trace("Application initializing");
        // Application-level resources and the native XAML surface must start
        // in the saved theme too, not just inherit it later from an inner Grid.
        // Default mode deliberately leaves Windows' app preference untouched.
        if (WinUiTheme.Mode == WinUiAppearanceMode.Dark)
            RequestedTheme = ApplicationTheme.Dark;
        else if (WinUiTheme.Mode == WinUiAppearanceMode.Light)
            RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        WinUiStartupDiagnostics.Trace("Application initialized");
        this.startInTray = startInTray;
        this.silenceDisconnectMilliseconds = silenceDisconnectMilliseconds;
        this.useAlac = useAlac;
        this.silenceMode = silenceMode;
        this.languageSelfTest = languageSelfTest;
        this.memorySelfTest = memorySelfTest;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (memorySelfTest)
        {
            WinUiMemoryTest.RunOnUiThread();
            return;
        }
        if (languageSelfTest)
        {
            SelfTest.CheckLanguageViews();
            Exit();
            return;
        }
        try
        {
            WinUiStartupDiagnostics.Trace("OnLaunched");
            main = new WinUiMainWindow(startInTray, silenceDisconnectMilliseconds, useAlac, silenceMode);
            WinUiStartupDiagnostics.Trace("Window constructed");
            main.InitializeLaunch(startInTray);
            WinUiStartupDiagnostics.Trace("OnLaunched complete");
        }
        catch (Exception error)
        {
            WinUiStartupDiagnostics.Error(error);
            WinUiNative.MessageBoxW(0, "AirStereo 界面启动失败，已尝试保存诊断日志。\n" + error.Message, "AirStereo", 0x10);
            throw;
        }
    }
}
