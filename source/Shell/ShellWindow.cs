using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.Graphics;

namespace AirStereo2;

internal sealed class ShellWindow : Window
{
    readonly BridgeClient client;
    readonly ShellView view;
    readonly WinUiTrayIcon tray;
    readonly LocalOutputMuteGuard muteGuard;
    readonly WinUiMotion motion = new();
    readonly DispatcherQueueTimer dismiss;
    readonly DispatcherQueueTimer focusWatch;
    SettingsWindow? settings;
    bool quitting, popupVisible;
    int visibilityVersion;
    readonly nint hwnd;
    ContentDialog? activeDialog;
    int activeDialogId;
    bool startupHandled;
    bool pinShowing;
    readonly HashSet<string> promptedPins = new();
    readonly Dictionary<string, (bool Connected, bool Preempted, bool Failed)> deviceStates = new(StringComparer.OrdinalIgnoreCase);

    internal ShellWindow()
    {
        Title = WinUiLanguage.T("AirStereo2 · 立体声 AirPlay 发送器");
        client = new BridgeClient(DispatcherQueue);
        muteGuard = new LocalOutputMuteGuard(DispatcherQueue);
        muteGuard.SystemVolumeChanged += percent => {
            if (!quitting && client.Ready)
                _ = client.Set("MasterVolume", percent, "main");
        };
        view = new ShellView(client);
        view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
        // Apply RequestedTheme before attaching Content to avoid a light-frame flash in dark mode.
        WinUiTheme.Apply(view);
        client.Dialog += OnDialog;
        client.Error += OnClientError;
        client.Changed += CheckPinRequests;
        client.Changed += SyncLocalMuteGuard;
        client.Changed += SyncTrayAndNotifications;
        client.Changed += async () => {
            if (startupHandled) return;
            startupHandled = true;
            await client.Set("LanguageIndex", WinUiLanguage.English ? 0 : 1);
            int savedProtocol = UiPreferences.Read("protocolMode", 0);
            if (savedProtocol is >= 0 and <= 2)
                await client.Send("setProtocol", value: savedProtocol);
            int savedSampleRate = UiPreferences.Read("sampleRateHz", 44100);
            await client.Send("setSampleRate", value: savedSampleRate == 48000 ? 48000 : 44100);
            await client.Send("initialize", value: (long)hwnd);
            await client.Send("startup", value: Environment.GetCommandLineArgs().Contains("--tray"));
            _ = InAppUpdater.CheckQuietlyIfEnabledAsync();
        };
        Content = view;
        view.Present(0);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        // Match AirStereo2 tray popup style: tool window (excluded from taskbar/Alt+Tab) + rounded corners + no non-client frame.
        try { AppWindow.IsShownInSwitchers = false; } catch { }
        long exStyle = WinUiNative.GetWindowLongPtrW(hwnd, -20).ToInt64();
        WinUiNative.SetWindowLongPtrW(hwnd, -20, (nint)((exStyle & ~0x40000L) | 0x80));
        int corners = 2;
        WinUiNative.DwmSetWindowAttribute(hwnd, 33, ref corners, 4);
        WinUiNative.RemoveNonClientFrame(hwnd);
        ApplyWindowTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AirStereo2.ico"));
        dismiss = DispatcherQueue.CreateTimer();
        dismiss.Interval = TimeSpan.FromMilliseconds(170);
        dismiss.IsRepeating = false;
        dismiss.Tick += (_, _) => {
            nint foreground = WinUiNative.GetForegroundWindow();
            if (ShouldDismissPopup(foreground)) HideWindow();
        };
        focusWatch = DispatcherQueue.CreateTimer();
        focusWatch.Interval = TimeSpan.FromMilliseconds(500);
        focusWatch.IsRepeating = true;
        focusWatch.Tick += (_, _) => {
            if (!quitting && popupVisible) {
                UpdateScanVisuals();
                ScheduleDismissIfOutside();
            }
        };
        focusWatch.Start();
        tray = new WinUiTrayIcon(TrayIconPath());
        tray.Toggle += () => { dismiss.Stop(); ToggleVisibility(); };
        tray.Open += Open;
        tray.Settings += OpenSettings;
        tray.QuickConnect += async () => await view.QuickConnectAsync();
        tray.StopPlayback += async () => await client.Command("DisconnectAllCommand", "main");
        tray.Exit += Quit;
        view.SettingsRequested += OpenSettings;
        view.HideRequested += () => HideWindow();
        view.VolumeApplied += () => motion.Pulse(view.ApplyVolumeIconControl);
        view.ScanStateChanged += UpdateScanVisuals;
        view.ActualThemeChanged += (_, _) => ApplyWindowTheme();
        view.Root.Loaded += (_, _) => {
            ApplyWindowTheme();
            if (popupVisible && !quitting) {
                motion.Enter(view.Root);
                UpdateScanVisuals();
            }
        };
        AppWindow.Closing += (_, e) => { if (!quitting) { e.Cancel = true; HideWindow(); } };
        Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.Deactivated) {
                if (settings == null && !tray.MenuOpen && !pinShowing && activeDialog == null)
                    dismiss.Start();
            } else {
                dismiss.Stop();
            }
        };
        WinUiTheme.Changed += OnThemeChanged;
        WinUiLanguage.Changed += OnLanguageChanged;
        UiPreferences.Changed += OnPreferenceChanged;
        SyncStartupRegistrationPath();
        Position();
    }

    static void SyncStartupRegistrationPath()
    {
        if (WinUiNative.IsPackaged()) return;
        try {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue("AirStereo2") is string existing) {
                string expected = "\"" + exe + "\" --tray";
                if (!string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
                    key.SetValue("AirStereo2", expected);
            }
        } catch { }
    }

    internal void InitializeLaunch(bool startupLaunch)
    {
        if (startupLaunch && UiPreferences.Read("silent", true) && tray.Registered) {
            popupVisible = false;
            AppWindow.Hide();
        } else {
            Open();
        }
    }

    static string TrayIconPath()
    {
        bool lightTaskbar = false;
        try {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        } catch { }
        return Path.Combine(AppContext.BaseDirectory, lightTaskbar ? "TrayDark.ico" : "TrayLight.ico");
    }

    bool ShouldDismissPopup(nint foreground)
    {
        nint owner = foreground == 0 ? 0 : WinUiNative.GetAncestor(foreground, 3);
        return popupVisible && !quitting && settings == null && !tray.MenuOpen && !pinShowing && activeDialog == null &&
            foreground != 0 && foreground != hwnd && owner != hwnd;
    }

    void ScheduleDismissIfOutside()
    {
        nint foreground = WinUiNative.GetForegroundWindow();
        if (ShouldDismissPopup(foreground)) {
            if (!dismiss.IsRunning) dismiss.Start();
        } else {
            dismiss.Stop();
        }
    }

    void Position() {
        var (work, scale) = tray.WorkArea();
        int margin = (int)Math.Round(12 * scale);
        int w = Math.Min((int)Math.Round(400 * scale), work.Right - work.Left - margin * 2);
        int h = Math.Min((int)Math.Round(512 * scale), work.Bottom - work.Top - margin * 2);
        AppWindow.MoveAndResize(new RectInt32(Math.Max(work.Left, work.Right - w - margin), Math.Max(work.Top, work.Bottom - h - margin), w, h));
    }

    void OnThemeChanged(WinUiAppearanceMode _) => DispatcherQueue.TryEnqueue(() => {
        if (quitting) return;
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
        tray.UpdateIcon(TrayIconPath());
    });

    void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() => {
        if (quitting) return;
        view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
        Title = WinUiLanguage.T("AirStereo2 · 立体声 AirPlay 发送器");
        tray.SetTip(WinUiLanguage.T("AirStereo2 · 立体声 AirPlay 发送器"));
    });

    void ApplyWindowTheme() {
        WinUiNative.ApplyWindowTheme(hwnd, WinUiTheme.IsDark(view));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
    }

    void UpdateScanVisuals() {
        if (!view.Root.IsLoaded) return;
        motion.Spin(view.RefreshIconControl, view.IsScanning && popupVisible && !quitting);
    }

    void ToggleVisibility() { if (popupVisible && WinUiNative.IsWindowVisible(hwnd)) HideWindow(); else Open(); }

    void Open() {
        if (quitting) return;
        if (popupVisible && WinUiNative.IsWindowVisible(hwnd)) {
            dismiss.Stop();
            Activate();
            WinUiNative.SetForegroundWindow(hwnd);
            return;
        }
        visibilityVersion++;
        popupVisible = true;
        dismiss.Stop();
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
        Position();
        motion.Reset(view.Root);
        AppWindow.Show();
        Activate();
        WinUiNative.SetForegroundWindow(hwnd);
        if (view.Root.IsLoaded) {
            motion.Enter(view.Root);
            UpdateScanVisuals();
        }
    }

    async void HideWindow(bool immediate = false) {
        dismiss.Stop();
        if (!popupVisible && !immediate) return;
        popupVisible = false;
        int version = ++visibilityVersion;
        motion.Spin(view.RefreshIconControl, false);
        int delay = immediate ? 0 : motion.Exit(view.Root);
        if (delay > 0) await Task.Delay(delay);
        if (quitting || version != visibilityVersion) return;
        AppWindow.Hide();
        motion.Reset(view.Root);
    }

    void OpenSettings() => OpenSettingsTab(null);

    void OpenSettingsTab(string? tab) {
        dismiss.Stop();
        if (settings == null) {
            var opened = new SettingsWindow(client, AppWindow.Id);
            settings = opened;
            opened.Closed += (_, _) => {
                if (ReferenceEquals(settings, opened)) settings = null;
                DispatcherQueue.TryEnqueue(ScheduleDismissIfOutside);
            };
        }
        settings.Open(tab);
    }

    void OnClientError(string message)
    {
        if (quitting || string.IsNullOrWhiteSpace(message)) return;
        if (message.Contains("AirPlay 1", StringComparison.OrdinalIgnoreCase) || message.Contains("AirPlay 2", StringComparison.OrdinalIgnoreCase)) {
            int protoMode = (int)client.Snapshot.Number("protocolMode", UiPreferences.Read("protocolMode", 0));
            string title = protoMode == 1 ? WinUiLanguage.T("AirPlay 1 连接失败") : WinUiLanguage.T("AirPlay 2 连接失败");
            _ = ShowProtocolFailureDialogAsync(title, message);
        }
    }

    async Task ShowProtocolFailureDialogAsync(string title, string body)
    {
        if (quitting || activeDialog != null || pinShowing) return;
        try {
            Open();
            var dialog = new ContentDialog {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
                PrimaryButtonText = WinUiLanguage.T("打开播放设置"),
                CloseButtonText = WinUiLanguage.T("知道了"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = settings?.RootForDialogs.XamlRoot ?? view.XamlRoot
            };
            activeDialog = dialog;
            var res = await UiDialogs.Show(dialog);
            if (res == ContentDialogResult.Primary)
                OpenSettingsTab("playback");
        } catch (Exception ex) {
            UiPreferences.Record(ex.ToString());
        } finally {
            activeDialog = null;
        }
    }

    async void OnDialog(System.Text.Json.JsonElement message) {
        int id = (int)message.Number("dialogId");
        switch (message.Text("kind")) {
            case "dialogState": if (activeDialog != null && activeDialogId == id) activeDialog.IsPrimaryButtonEnabled = message.Flag("primaryEnabled"); return;
            case "dialogClosed": if (activeDialogId == id) activeDialog?.Hide(); return;
            case "dialog": break;
            default: return;
        }
        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        foreach (var field in message.List("fields")) {
            string key = ((int)field.Number("id")).ToString();
            switch (field.Text("type")) {
                case "text": panel.Children.Add(ShellView.Text(field.Text("text"))); break;
                case "input":
                    var input = new TextBox { Text = field.Text("text"), MaxLength = (int)field.Number("maxLength"), AcceptsReturn = field.Flag("multiline"), TextWrapping = TextWrapping.Wrap };
                    input.TextChanged += async (_, _) => await client.Send("dialogInput", name: key, value: input.Text, index: id);
                    panel.Children.Add(input); break;
                case "password":
                    var password = new PasswordBox { MaxLength = (int)field.Number("maxLength", 256) };
                    password.PasswordChanged += async (_, _) => await client.Send("dialogInput", name: key, value: password.Password, index: id);
                    panel.Children.Add(password); break;
                case "check":
                    var check = new CheckBox { Content = field.Text("text"), IsChecked = field.Flag("value") };
                    check.Click += async (_, _) => await client.Send("dialogInput", name: key, value: check.IsChecked == true, index: id);
                    panel.Children.Add(check); break;
            }
        }
        try {
            var dialog = new ContentDialog { Title = message.Text("title"), Content = new ScrollViewer { Content = panel, MaxHeight = 430 },
                PrimaryButtonText = message.Text("primary"), SecondaryButtonText = message.Text("secondary"), CloseButtonText = message.Text("close", WinUiLanguage.T("关闭")),
                IsPrimaryButtonEnabled = message.Flag("primaryEnabled"), XamlRoot = settings?.RootForDialogs.XamlRoot ?? view.XamlRoot };
            activeDialog = dialog; activeDialogId = id;
            if (settings == null) Open(); else settings.Activate();
            var result = await UiDialogs.Show(dialog);
            await client.Send("dialogResult", value: result == ContentDialogResult.Primary ? "Primary" : result == ContentDialogResult.Secondary ? "Secondary" : "None", index: id);
        } catch (Exception ex) { UiPreferences.Record(ex.ToString()); await client.Send("dialogResult", value: "None", index: id); }
        finally { activeDialog = null; }
    }

    async void CheckPinRequests() {
        var devices = client.Snapshot.List("devices");
        promptedPins.RemoveWhere(id => !devices.Any(d => d.Text("id") == id && d.Flag("IsPinRequested")));
        if (pinShowing) return;
        var target = devices.FirstOrDefault(d => d.Flag("IsPinRequested") && !promptedPins.Contains(d.Text("id")));
        if (target.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        string id = target.Text("id"); promptedPins.Add(id); pinShowing = true;
        try {
            var input = new TextBox { Header = WinUiLanguage.T("音响屏幕上的配对码"), MaxLength = 12 };
            Open();
            var dialog = new ContentDialog { Title = target.Text("Name"), Content = input, PrimaryButtonText = WinUiLanguage.T("确认配对"), CloseButtonText = WinUiLanguage.T("取消"), XamlRoot = view.XamlRoot };
            if (await UiDialogs.Show(dialog) == ContentDialogResult.Primary) {
                await client.Set("PinCode", input.Text, id); await client.Command("SubmitPinCommand", id);
            } else await client.Command("CancelPinCommand", id);
        } finally { pinShowing = false; }
    }

    void OnPreferenceChanged(string key) => DispatcherQueue.TryEnqueue(() => {
        if (quitting) return;
        if (key == "syncSystemVolume") SyncLocalMuteGuard();
    });

    void SyncLocalMuteGuard()
    {
        if (quitting || !client.Ready) return;
        bool muteEnabled = client.Snapshot.Field("settings").Flag("MuteLocalOutputWhileStreaming");
        bool syncVolumeEnabled = UiPreferences.Read("syncSystemVolume", false);
        bool streaming = client.Snapshot.List("devices").Any(d => d.Flag("IsConnected"));
        muteGuard.Update(muteEnabled, syncVolumeEnabled, streaming);
    }

    void SyncTrayAndNotifications()
    {
        if (quitting || !client.Ready) return;
        var devices = client.Snapshot.List("devices");
        var connected = devices.Where(d => d.Flag("IsConnected")).ToArray();
        var connecting = devices.Where(d => d.Flag("IsConnecting")).ToArray();
        var preempted = devices.Where(d => d.Flag("IsPreempted")).ToArray();
        int volume = (int)Math.Round(client.Snapshot.Number("masterVolume", 50));

        tray.IsPlaybackActive = connected.Length > 0 || connecting.Length > 0;
        tray.CanQuickConnect = view.HasQuickConnectTarget;

        bool autoReconnect = client.Snapshot.Field("settings").Flag("AutomaticReceiverRecoveryEnabled");
        if (connected.Length > 0) {
            string names = string.Join("、", connected.Select(d => d.Text("Name", WinUiLanguage.T("未知音响"))));
            tray.SetTip(string.Format(WinUiLanguage.T("AirStereo2 · 播放中：{0} · {1}%"), names, volume));
        } else if (connecting.Length > 0) {
            string names = string.Join("、", connecting.Select(d => d.Text("Name", WinUiLanguage.T("未知音响"))));
            tray.SetTip(string.Format(WinUiLanguage.T("AirStereo2 · 连接中：{0}…"), names));
        } else if (preempted.Length > 0) {
            string names = string.Join("、", preempted.Select(d => d.Text("Name", WinUiLanguage.T("未知音响"))));
            tray.SetTip(string.Format(WinUiLanguage.T(autoReconnect ? "AirStereo2 · 已被接管，正在重连：{0}" : "AirStereo2 · 已被接管：{0}"), names));
        } else {
            tray.SetTip(WinUiLanguage.T("AirStereo2 · 立体声 AirPlay 发送器"));
        }

        bool notifyEnabled = UiPreferences.Read("notifications", true);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) {
            string key = d.Text("key");
            if (string.IsNullOrEmpty(key)) key = d.Text("id");
            seenKeys.Add(key);
            string name = d.Text("Name", WinUiLanguage.T("未知音响"));
            bool isConn = d.Flag("IsConnected");
            bool isPre = d.Flag("IsPreempted");
            bool isFail = d.Flag("IsFailed");
            deviceStates.TryGetValue(key, out var prev);
            deviceStates[key] = (isConn, isPre, isFail);

            if (isFail && !prev.Failed) {
                int protoMode = (int)client.Snapshot.Number("protocolMode", UiPreferences.Read("protocolMode", 0));
                string reason = d.Text("FailureMessage").Trim();
                if (protoMode == 1 || protoMode == 2) {
                    string protoName = protoMode == 1 ? "AirPlay 1" : "AirPlay 2";
                    string title = string.Format(WinUiLanguage.T("{0} 连接失败"), protoName);
                    string detail = string.IsNullOrEmpty(reason)
                        ? string.Format(WinUiLanguage.T("音响「{0}」未能通过 {1} 建立连接。\n\n当前保持在所选协议模式，不会自动切换。如需自动匹配协议，请前往「设置 → 播放与音频」切换为“自动推荐”。"), name, protoName)
                        : string.Format(WinUiLanguage.T("音响「{0}」未能通过 {1} 建立连接：\n{2}\n\n当前保持在所选协议模式，不会自动切换。如需自动匹配协议，请前往「设置 → 播放与音频」切换为“自动推荐”。"), name, protoName, reason);
                    _ = ShowProtocolFailureDialogAsync(title, detail);
                }
                if (notifyEnabled && (prev.Connected || !popupVisible)) {
                    string msg = string.IsNullOrEmpty(reason)
                        ? string.Format(WinUiLanguage.T("音响「{0}」连接中断，请检查网络或重新连接。"), name)
                        : string.Format(WinUiLanguage.T("音响「{0}」连接失败：{1}"), name, reason);
                    tray.ShowBalloon("AirStereo2", msg, 3);
                }
            } else if (notifyEnabled && isPre && !prev.Preempted) {
                string msg = autoReconnect
                    ? string.Format(WinUiLanguage.T("音响「{0}」已被其他设备接管，空闲后将自动重连。"), name)
                    : string.Format(WinUiLanguage.T("音响「{0}」已被其他设备接管，电脑串流已暂停。"), name);
                tray.ShowBalloon("AirStereo2", msg, 2);
            }
        }
        foreach (string stale in deviceStates.Keys.Where(k => !seenKeys.Contains(k)).ToArray())
            deviceStates.Remove(stale);
    }

    void Quit() {
        if (quitting) return;
        quitting = true;
        visibilityVersion++;
        dismiss.Stop();
        focusWatch.Stop();
        muteGuard.Dispose();
        motion.Dispose();
        WinUiTheme.Changed -= OnThemeChanged;
        WinUiLanguage.Changed -= OnLanguageChanged;
        UiPreferences.Changed -= OnPreferenceChanged;
        settings?.Close();
        tray.Dispose();
        client.Dispose();
        Close(); Application.Current.Exit();
    }
}
