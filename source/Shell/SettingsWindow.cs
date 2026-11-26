using System.IO;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AirStereo2;

// Presentation only: every audio setting still invokes the original service.
internal sealed class SettingsWindow : Window
{
    readonly BridgeClient client;
    readonly SettingsView view = new();
    readonly WinUiMotion motion = new();
    readonly List<Action<JsonElement>> bindings = new();
    readonly Dictionary<string, (int Value, DateTime UntilUtc)> pendingChoices = new(StringComparer.Ordinal);
    readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer sizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly HashSet<uint> logPointers = new();
    readonly Slider[] eqBands = new Slider[10];
    readonly TextBlock[] eqValues = new TextBlock[10], eqLabels = new TextBlock[10];
    readonly nint hwnd;
    readonly WindowId anchor;
    DateTime lastInteraction = DateTime.MinValue;
    string latestLog = "", renderedLog = "", lastError = "", presetNames = "", defaultSpeakersKey = "";
    string releaseTargetUrl = InAppUpdater.ReleasesPageUrl;
    UpdateCheckResult? availableUpdate;
    CancellationTokenSource? downloadCts;
    bool updating, closed, positioning, hasOpened, applyingPreset, checkingUpdates, downloadingUpdate, followPending = true;

    FrameworkElement Root => view.Control<Grid>("SettingsRoot");
    internal FrameworkElement RootForDialogs => view;
    T Control<T>(string name) where T : DependencyObject => view.Control<T>(name);

    internal SettingsWindow(BridgeClient bridge, WindowId owner)
    {
        client = bridge; anchor = owner;
        Title = WinUiLanguage.T("AirStereo2 · 设置");
        view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
        WinUiTheme.Apply(view); Content = view;
        SystemBackdrop = new MicaBackdrop();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyWindowTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AirStereo2.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = true;
        // Hide until Open() positions and activates with the persisted theme applied.
        WinUiNative.ShowWindow(hwnd, 0);
        Position();
        var nav = Control<NavigationView>("SettingsNavigation");
        nav.OpenPaneLength = WinUiLanguage.English ? 196 : 156;
        nav.SelectionChanged += (_, args) => {
            string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";
            FrameworkElement? activePage = null;
            foreach (var (key, name) in new[] {
                ("general", "GeneralPage"),
                ("playback", "PlaybackPage"),
                ("audio", "AudioPage"),
                ("diagnostics", "DiagnosticsPage"),
                ("about", "AboutPage")
            }) {
                var page = Control<FrameworkElement>(name);
                page.Visibility = key == tag ? Visibility.Visible : Visibility.Collapsed;
                if (key == tag) activePage = page;
            }
            if (tag == "diagnostics") followPending = true;
            if (activePage != null && Root.IsLoaded) motion.Enter(activePage, 6);
            Refresh();
        };
        ConfigureGeneral();
        ConfigurePlayback();
        ConfigureEqualizer();
        ConfigureDiagnostics();
        ConfigureAboutAndUpdates();
        nav.SelectedItem = nav.MenuItems[0];
        Root.Loaded += (_, _) => {
            ApplyWindowTheme();
            motion.Enter(Root, 6);
        };
        client.Changed += Refresh; client.Error += Error;
        WinUiTheme.Changed += OnThemeChanged; WinUiLanguage.Changed += LanguageChanged;
        view.ActualThemeChanged += ActualThemeChanged;
        AppWindow.Changed += (_, args) => {
            if (args.DidSizeChange && hasOpened && !positioning && !closed) {
                sizeSaveTimer.Stop();
                sizeSaveTimer.Start();
            }
        };
        AppWindow.Closing += (_, _) => SaveWindowSize();
        sizeSaveTimer.Tick += (_, _) => { sizeSaveTimer.Stop(); SaveWindowSize(); };
        Closed += (_, _) => {
            SaveWindowSize();
            closed = true; logTimer.Stop(); sizeSaveTimer.Stop(); motion.Dispose();
            try { downloadCts?.Cancel(); downloadCts?.Dispose(); } catch { }
            downloadCts = null;
            client.Changed -= Refresh; client.Error -= Error;
            WinUiTheme.Changed -= OnThemeChanged; WinUiLanguage.Changed -= LanguageChanged;
            view.ActualThemeChanged -= ActualThemeChanged;
            foreach (var name in new[] { "LanguageChoice", "Appearance", "ProtocolChoice", "Codec", "SampleRateChoice", "Latency", "EqualizerPreset" })
                Control<BottomComboBox>(name).Dispose();
        };
        logTimer.Tick += (_, _) => FollowLogs(); logTimer.Start();
        ApplyWindowTheme(); Refresh();
    }

    double WindowScale()
    {
        if (view.XamlRoot is { RasterizationScale: > 0 } root) return root.RasterizationScale;
        try {
            uint dpi = WinUiNative.GetDpiForWindow(hwnd);
            if (dpi >= 96) return dpi / 96.0;
        } catch { }
        return 1.0;
    }

    void SaveWindowSize()
    {
        if (closed || positioning || !hasOpened) return;
        try {
            if (AppWindow.Presenter is OverlappedPresenter p && p.State != OverlappedPresenterState.Restored)
                return;
            var size = AppWindow.Size;
            double scale = WindowScale();
            if (scale <= 0 || size.Width <= 0 || size.Height <= 0) return;
            int dipW = (int)Math.Round(size.Width / scale);
            int dipH = (int)Math.Round(size.Height / scale);
            if (dipW < 500 || dipH < 380) return;
            dipW = Math.Clamp(dipW, 620, 2400);
            dipH = Math.Clamp(dipH, 480, 1800);
            if (UiPreferences.Read("settingsWidth", 860) != dipW)
                UiPreferences.Set("settingsWidth", dipW);
            if (UiPreferences.Read("settingsHeight", 660) != dipH)
                UiPreferences.Set("settingsHeight", dipH);
        } catch { }
    }

    void Position()
    {
        positioning = true;
        try {
            var area = DisplayArea.GetFromWindowId(anchor, DisplayAreaFallback.Nearest).WorkArea;
            double scale = WindowScale();
            int margin = (int)Math.Round(16 * scale);
            int savedW = Math.Clamp(UiPreferences.Read("settingsWidth", 860), 620, 2400);
            int savedH = Math.Clamp(UiPreferences.Read("settingsHeight", 660), 480, 1800);
            int maxW = Math.Max(480, area.Width - margin * 2);
            int maxH = Math.Max(360, area.Height - margin * 2);
            int minW = Math.Min((int)Math.Round(620 * scale), maxW);
            int minH = Math.Min((int)Math.Round(480 * scale), maxH);
            int width = Math.Clamp((int)Math.Round(savedW * scale), minW, maxW);
            int height = Math.Clamp((int)Math.Round(savedH * scale), minH, maxH);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        } finally {
            positioning = false;
        }
    }

    internal void Open(string? initialTab = null)
    {
        if (!string.IsNullOrWhiteSpace(initialTab)) {
            var nav = Control<NavigationView>("SettingsNavigation");
            var match = nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => string.Equals(i.Tag as string, initialTab, StringComparison.OrdinalIgnoreCase));
            if (match != null) nav.SelectedItem = match;
        }
        if (!hasOpened) Position();
        hasOpened = true;
        AppWindow.Show(true);
        WinUiNative.ShowWindow(hwnd, 9);
        Activate();
        WinUiNative.SetForegroundWindow(hwnd);
        ApplyWindowTheme();
        if (Root.IsLoaded) motion.Enter(Root, 6);
        Refresh();
    }

    void OnThemeChanged(WinUiAppearanceMode _) => DispatcherQueue.TryEnqueue(() => {
        if (closed) return;
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
    });

    void ActualThemeChanged(FrameworkElement sender, object args) => ApplyWindowTheme();

    void ApplyTitleBarTheme()
    {
        try {
            bool dark = WinUiTheme.IsDark(view);
            var fill = dark ? ColorHelper.FromArgb(255, 31, 31, 31) : ColorHelper.FromArgb(255, 243, 243, 247);
            var foreground = dark ? Colors.White : Colors.Black;
            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = fill;
            titleBar.InactiveBackgroundColor = fill;
            titleBar.ButtonBackgroundColor = fill;
            titleBar.ButtonInactiveBackgroundColor = fill;
            titleBar.ButtonHoverBackgroundColor = dark
                ? ColorHelper.FromArgb(255, 55, 55, 55)
                : ColorHelper.FromArgb(255, 225, 225, 230);
            titleBar.ButtonPressedBackgroundColor = dark
                ? ColorHelper.FromArgb(255, 72, 72, 72)
                : ColorHelper.FromArgb(255, 210, 210, 216);
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = foreground;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = foreground;
        } catch { }
    }

    void ApplyWindowTheme()
    {
        bool dark = WinUiTheme.IsDark(view);
        WinUiNative.ApplyWindowTheme(hwnd, dark);
        ApplyTitleBarTheme();
    }

    void LanguageChanged()
    {
        DispatcherQueue.TryEnqueue(() => {
            if (closed) return;
            view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
            Title = WinUiLanguage.T("AirStereo2 · 设置");
            Control<NavigationView>("SettingsNavigation").OpenPaneLength = WinUiLanguage.English ? 196 : 156;
            presetNames = "";
            defaultSpeakersKey = "";
            renderedLog = "";
            followPending = true;
            UpdateAboutStaticTexts();
            Refresh();
        });
    }

    void Error(string message) { lastError = message; Control<TextBlock>("LatestNotice").Text = lastError; RenderDiagnostics(); }
    static bool Editing(Control control) => control.FocusState != FocusState.Unfocused;

    void Toggle(string name, string key)
    {
        var box = Control<CheckBox>(name);
        bindings.Add(s => { if (!Editing(box)) box.IsChecked = s.Flag(key); });
        box.Click += async (_, _) => { if (!updating) await client.Set(key, box.IsChecked == true); };
    }

    int ResolveChoiceIndex(string key, int snapshotIndex)
    {
        if (pendingChoices.TryGetValue(key, out var pending)) {
            if (snapshotIndex == pending.Value || DateTime.UtcNow >= pending.UntilUtc)
                pendingChoices.Remove(key);
            else
                return pending.Value;
        }
        return snapshotIndex;
    }

    void MarkChoicePending(string key, int value) =>
        pendingChoices[key] = (value, DateTime.UtcNow.AddSeconds(2.5));

    void Choice(string name, string key, params string[] choices)
    {
        var box = Control<BottomComboBox>(name);
        bool? english = null;
        bindings.Add(s => {
            if (english != WinUiLanguage.English) { english = WinUiLanguage.English; box.ItemsSource = choices.Select(WinUiLanguage.T).ToArray(); }
            int idx = ResolveChoiceIndex(key, (int)s.Number(key, -1));
            if (!Editing(box)) box.SelectedIndex = idx;
        });
        box.SelectionChanged += async (_, _) => {
            if (!updating && box.SelectedIndex >= 0) {
                MarkChoicePending(key, box.SelectedIndex);
                await client.Set(key, box.SelectedIndex);
            }
        };
    }

    void ConfigureGeneral()
    {
        var language = Control<BottomComboBox>("LanguageChoice");
        language.ItemsSource = new[] { "中文（简体）", "English" };
        language.SelectedIndex = WinUiLanguage.English ? 1 : 0;
        language.SelectionChanged += async (_, _) => {
            if (updating || language.SelectedIndex < 0) return;
            WinUiLanguage.Set(language.SelectedIndex == 1);
            await client.Set("LanguageIndex", language.SelectedIndex == 1 ? 0 : 1);
        };

        var appearance = Control<BottomComboBox>("Appearance");
        bool? appearanceEnglish = null;
        bindings.Add(_ => {
            if (appearanceEnglish != WinUiLanguage.English) {
                appearanceEnglish = WinUiLanguage.English;
                appearance.ItemsSource = new[] { "跟随系统", "浅色", "深色" }.Select(WinUiLanguage.T).ToArray();
            }
            if (!Editing(appearance)) appearance.SelectedIndex = (int)WinUiTheme.Mode;
        });
        appearance.SelectionChanged += (_, _) => {
            if (updating || appearance.SelectedIndex < 0) return;
            try {
                WinUiTheme.Set((WinUiAppearanceMode)appearance.SelectedIndex);
                WinUiLanguage.SetText(Control<TextBlock>("AppearanceStatus"), "外观已应用并保存。");
            } catch (Exception ex) {
                WinUiLanguage.Message(Control<TextBlock>("AppearanceStatus"), "外观已应用但无法保存：{0}", ex.Message);
            }
        };

        var startup = Control<CheckBox>("Startup");
        if (WinUiNative.IsPackaged()) {
            startup.IsEnabled = false;
            _ = LoadPackagedStartupAsync(startup);
            startup.Click += async (_, _) => await TogglePackagedStartupAsync(startup);
        } else {
            startup.IsChecked = StartupEnabled();
            startup.Click += (_, _) => {
                try { SetStartup(startup.IsChecked == true); }
                catch (Exception ex) { Error(ex.Message); startup.IsChecked = StartupEnabled(); }
            };
        }

        Toggle("SilentStartup", "SilentStartEnabled");

        var notifyBox = Control<CheckBox>("ConnectionNotifications");
        notifyBox.IsChecked = UiPreferences.Read("notifications", true);
        notifyBox.Click += (_, _) => UiPreferences.Set("notifications", notifyBox.IsChecked == true);

        var autoConnect = Control<CheckBox>("AutoConnect");
        bindings.Add(s => { if (!Editing(autoConnect)) autoConnect.IsChecked = s.Flag("AutoConnectOnLaunch"); });
        autoConnect.Click += async (_, _) => {
            if (updating) return;
            bool enabled = autoConnect.IsChecked == true;
            await client.Set("AutoConnectOnLaunch", enabled);
            if (enabled && client.Snapshot.List("defaultOutputs").Length == 0) {
                foreach (var connected in client.Snapshot.List("devices").Where(d => d.Flag("IsConnected")))
                    await client.Set("IsDefaultOutput", true, connected.Text("id"));
            }
        };
        Control<Button>("RefreshDefaultSpeakers").Click += async (_, _) => await client.Command("RefreshCommand", "home");
        bindings.Add(_ => RenderDefaultSpeakers());
    }

    void ConfigurePlayback()
    {
        var protocolBox = Control<BottomComboBox>("ProtocolChoice");
        bool? protocolEnglish = null;
        string[] protocolChoices = { "自动推荐", "AirPlay 1 · 兼容模式", "AirPlay 2 · 原生模式" };
        bindings.Add(_ => {
            if (protocolEnglish != WinUiLanguage.English) {
                protocolEnglish = WinUiLanguage.English;
                protocolBox.ItemsSource = protocolChoices.Select(WinUiLanguage.T).ToArray();
            }
            int snapshotProto = (int)client.Snapshot.Number("protocolMode", UiPreferences.Read("protocolMode", 0));
            int currentProto = ResolveChoiceIndex("protocolMode", snapshotProto);
            if (!Editing(protocolBox)) protocolBox.SelectedIndex = Math.Clamp(currentProto, 0, 2);
            bool playing = client.Snapshot.List("devices").Any(d => d.Flag("IsConnected") || d.Flag("IsConnecting"));
            protocolBox.IsEnabled = !playing;
        });
        protocolBox.SelectionChanged += async (_, _) => {
            if (updating || protocolBox.SelectedIndex < 0) return;
            int mode = Math.Clamp(protocolBox.SelectedIndex, 0, 2);
            MarkChoicePending("protocolMode", mode);
            UiPreferences.Set("protocolMode", mode);
            await client.Send("setProtocol", value: mode);
        };

        Choice("Codec", "AudioCodecIndex", "ALAC·无损压缩", "PCM·未压缩");

        var sampleRateBox = Control<BottomComboBox>("SampleRateChoice");
        bool? sampleRateEnglish = null;
        string[] sampleRateChoices = { "44.1 kHz · 标准兼容", "48 kHz · 视频直通" };
        bindings.Add(_ => {
            if (sampleRateEnglish != WinUiLanguage.English) {
                sampleRateEnglish = WinUiLanguage.English;
                sampleRateBox.ItemsSource = sampleRateChoices.Select(WinUiLanguage.T).ToArray();
            }
            int snapHz = (int)client.Snapshot.Number("sampleRateHz", UiPreferences.Read("sampleRateHz", 44100));
            int snapIdx = snapHz == 48000 ? 1 : 0;
            int idx = ResolveChoiceIndex("sampleRateHz", snapIdx);
            if (!Editing(sampleRateBox)) sampleRateBox.SelectedIndex = idx;
            bool playing = client.Snapshot.List("devices").Any(d => d.Flag("IsConnected") || d.Flag("IsConnecting"));
            sampleRateBox.IsEnabled = !playing;
        });
        sampleRateBox.SelectionChanged += async (_, _) => {
            if (updating || sampleRateBox.SelectedIndex < 0) return;
            int idx = sampleRateBox.SelectedIndex == 1 ? 1 : 0;
            int hz = idx == 1 ? 48000 : 44100;
            MarkChoicePending("sampleRateHz", idx);
            UiPreferences.Set("sampleRateHz", hz);
            await client.Send("setSampleRate", value: hz);
        };

        var latencyBox = Control<BottomComboBox>("Latency");
        bool? latencyEnglish = null;
        string[] latencyChoices = { "自动", "标准 · 200 ms", "均衡 · 300 ms", "稳定 · 500 ms", "自定义" };
        var latency = Control<Slider>("CustomLatency");
        var customLatencyRow = Control<Grid>("CustomLatencyRow");
        bindings.Add(s => {
            if (latencyEnglish != WinUiLanguage.English) {
                latencyEnglish = WinUiLanguage.English;
                latencyBox.ItemsSource = latencyChoices.Select(WinUiLanguage.T).ToArray();
            }
            int mode = (int)s.Number("StreamingModeIndex");
            int customMs = (int)Math.Round(s.Number("CustomBufferSize", 200));
            int tier = ResolveChoiceIndex("latencyTier", ResolveLatencyTier(mode, customMs));
            if (!Editing(latencyBox)) latencyBox.SelectedIndex = tier;
            if (!Editing(latency)) latency.Value = tier switch {
                0 => s.Number("CurrentLatencyMs") is double req and > 0 ? req : 200,
                1 => 200,
                2 => 300,
                3 => 500,
                _ => customMs
            };
            Control<TextBlock>("CustomLatencyValue").Text = Math.Round(latency.Value) + " ms";
            customLatencyRow.Visibility = tier == 4 ? Visibility.Visible : Visibility.Collapsed;
            bool playing = client.Snapshot.List("devices").Any(d => d.Flag("IsConnected") || d.Flag("IsConnecting"));
            latencyBox.IsEnabled = !playing;
            Control<BottomComboBox>("Codec").IsEnabled = !playing;
            latency.IsEnabled = !playing && tier == 4;
        });
        latencyBox.SelectionChanged += async (_, _) => {
            if (updating || latencyBox.SelectedIndex < 0) return;
            int tier = latencyBox.SelectedIndex;
            MarkChoicePending("latencyTier", tier);
            UiPreferences.Set("latencyTier", tier);
            customLatencyRow.Visibility = tier == 4 ? Visibility.Visible : Visibility.Collapsed;
            switch (tier) {
                case 0:
                    await client.Set("StreamingModeIndex", 0);
                    break;
                case 1:
                    await client.Set("StreamingModeIndex", 1);
                    break;
                case 2:
                    await client.Set("CustomBufferSize", 300);
                    await client.Set("StreamingModeIndex", 3);
                    break;
                case 3:
                    await client.Set("StreamingModeIndex", 2);
                    break;
                default:
                    await client.Set("CustomBufferSize", (int)Math.Round(latency.Value));
                    await client.Set("StreamingModeIndex", 3);
                    break;
            }
        };
        latency.ValueChanged += async (_, _) => {
            Control<TextBlock>("CustomLatencyValue").Text = Math.Round(latency.Value) + " ms";
            if (!updating && latencyBox.SelectedIndex == 4) {
                UiPreferences.Set("latencyTier", 4);
                await client.Set("CustomBufferSize", (int)Math.Round(latency.Value));
            }
        };

        Toggle("MuteLocal", "MuteLocalOutputWhileStreaming");
        var syncSysVol = Control<CheckBox>("SyncSystemVolume");
        syncSysVol.IsChecked = UiPreferences.Read("syncSystemVolume", false);
        syncSysVol.Click += (_, _) => UiPreferences.Set("syncSystemVolume", syncSysVol.IsChecked == true);
        Toggle("Reconnect", "AutomaticReceiverRecoveryEnabled");
    }

    void ConfigureDiagnostics()
    {
        Control<Button>("ExportDiagnostics").Click += async (_, _) => {
            var btn = Control<Button>("ExportDiagnostics");
            btn.IsEnabled = false;
            try {
                var nowForName = WinUiLanguage.English
                    ? DateTimeOffset.UtcNow
                    : DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
                string defaultName = $"airstereo2-diagnostics-{nowForName:yyyyMMdd-HHmmss}.txt";
                string title = WinUiLanguage.English ? "Save diagnostics report" : "保存故障诊断报告";
                string? picked = WinUiNative.PickSaveTextFile(hwnd, title, defaultName, WinUiLanguage.English);
                if (string.IsNullOrWhiteSpace(picked) || closed) return;
                var reply = await client.SendReply("exportDiagnostics", name: WinUiLanguage.English ? "en-US" : "zh-CN", value: picked);
                string saved = reply?.Text("path") ?? "";
                if (!closed && !string.IsNullOrWhiteSpace(saved)) {
                    var dialog = new ContentDialog {
                        Title = WinUiLanguage.T("故障诊断已导出"),
                        Content = new TextBlock { Text = string.Format(WinUiLanguage.T("诊断报告已保存至：\n{0}"), saved), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                        CloseButtonText = WinUiLanguage.T("关闭"),
                        XamlRoot = view.XamlRoot
                    };
                    await UiDialogs.Show(dialog);
                }
            } catch (Exception ex) {
                if (!closed) {
                    var dialog = new ContentDialog {
                        Title = WinUiLanguage.T("导出失败"),
                        Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                        CloseButtonText = WinUiLanguage.T("关闭"),
                        XamlRoot = view.XamlRoot
                    };
                    await UiDialogs.Show(dialog);
                }
            } finally {
                if (!closed) btn.IsEnabled = true;
            }
        };
        Control<Button>("OpenDiagnostics").Click += async (_, _) => await client.Command("OpenLogsFolderCommand");
        Control<Button>("CheckNetwork").Click += async (_, _) => {
            var btn = Control<Button>("CheckNetwork");
            btn.IsEnabled = false;
            try { await client.Command("CheckNetworkCommand"); }
            finally { if (!closed) btn.IsEnabled = true; }
        };
        Control<Button>("FixFirewall").Click += async (_, _) => {
            var btn = Control<Button>("FixFirewall");
            btn.IsEnabled = false;
            try { await client.Command("FixFirewallCommand"); }
            finally { if (!closed) Refresh(); }
        };

        var scroller = Control<ScrollViewer>("RecentActivityScroll");
        void Touch() { lastInteraction = DateTime.UtcNow; followPending = true; }
        scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => Touch()), true);
        scroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { logPointers.Add(e.Pointer.PointerId); Touch(); }), true);
        scroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => { logPointers.Remove(e.Pointer.PointerId); Touch(); }), true);
        scroller.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => { logPointers.Remove(e.Pointer.PointerId); Touch(); }), true);
        scroller.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => { logPointers.Remove(e.Pointer.PointerId); Touch(); }), true);
        scroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => { if (logPointers.Count > 0) Touch(); }), true);
        scroller.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Touch()), true);
    }

    void ConfigureAboutAndUpdates()
    {
        bool storeEdition = InAppUpdater.IsStoreEdition;
        var autoNotify = Control<CheckBox>("AutoUpdateNotify");
        var checkBtn = Control<Button>("CheckUpdates");
        var applyBtn = Control<Button>("ApplyUpdate");
        var storeBtn = Control<Button>("StoreUpdate");
        var openBtn = Control<Button>("UpdateDownload");

        if (storeEdition) {
            autoNotify.Visibility = Visibility.Collapsed;
            checkBtn.Visibility = Visibility.Collapsed;
            applyBtn.Visibility = Visibility.Collapsed;
            storeBtn.Visibility = Visibility.Visible;
            storeBtn.Click += (_, _) => TryOpenUrl(InAppUpdater.StoreProductUrl);
        } else {
            autoNotify.Visibility = Visibility.Visible;
            autoNotify.IsChecked = UiPreferences.Read("autoUpdateNotify", false);
            autoNotify.Click += (_, _) => {
                bool on = autoNotify.IsChecked == true;
                UiPreferences.Set("autoUpdateNotify", on);
                InAppUpdater.NotifyPreferenceChanged();
                if (on) _ = InAppUpdater.CheckQuietlyIfEnabledAsync(force: true);
            };
            checkBtn.Visibility = Visibility.Visible;
            storeBtn.Visibility = Visibility.Collapsed;
            checkBtn.Click += async (_, _) => await CheckForUpdatesAsync();
            applyBtn.Click += async (_, _) => await DownloadOrCancelUpdateAsync();

            if (InAppUpdater.LatestUpdate is { Ok: true, HasUpdate: true } cached) {
                availableUpdate = cached;
                releaseTargetUrl = cached.ReleaseUrl;
                applyBtn.Visibility = Visibility.Visible;
            }
        }

        openBtn.Click += (_, _) => TryOpenUrl(releaseTargetUrl);
        Control<Button>("OpenRepo").Click += (_, _) => TryOpenUrl(InAppUpdater.RepoHomeUrl);
        Control<Button>("OpenIssues").Click += (_, _) => TryOpenUrl(InAppUpdater.IssuesUrl);
        Control<Button>("SupportTea").Click += async (_, _) => await ShowSupportTeaDialogAsync();
        UpdateAboutStaticTexts();
    }

    async Task ShowSupportTeaDialogAsync()
    {
        if (closed || view.XamlRoot is null) return;
        string qrPath = Path.Combine(AppContext.BaseDirectory, "SupportTeaQr.png");
        FrameworkElement content;
        if (File.Exists(qrPath)) {
            var panel = new StackPanel {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(new Image {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(qrPath)),
                Width = 280,
                Height = 334,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock {
                Text = WinUiLanguage.T("感谢你喜欢 AirStereo2！如果它帮到了你，欢迎微信扫码请作者喝杯茶。"),
                Style = ShellView.Style("AirStereo2SecondaryTextStyle"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 290,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content = panel;
        } else {
            content = new TextBlock {
                Text = WinUiLanguage.T("二维码图片缺失，请重新安装软件。"),
                Style = ShellView.Style("AirStereo2SecondaryTextStyle"),
                TextWrapping = TextWrapping.Wrap
            };
        }
        var dialog = new ContentDialog {
            Title = WinUiLanguage.T("请作者喝茶"),
            Content = content,
            CloseButtonText = WinUiLanguage.T("关闭"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = view.XamlRoot
        };
        await UiDialogs.Show(dialog);
    }

    void UpdateAboutStaticTexts()
    {
        Control<TextBlock>("AboutVersion").Text = $"AirStereo2 {InAppUpdater.CurrentAppVersion}";
        if (InAppUpdater.IsStoreEdition) {
            WinUiLanguage.SetText(Control<TextBlock>("DistributionInfo"), "当前渠道：Microsoft Store 版");
            WinUiLanguage.SetText(Control<TextBlock>("UpdateStatus"), "Microsoft Store 版本由微软商店自动管理更新，点击上方按钮可前往商店检查。");
        } else {
            WinUiLanguage.SetText(Control<TextBlock>("DistributionInfo"), "当前渠道：GitHub 桌面版（MSI · 支持应用内下载更新）");
            WinUiLanguage.Message(Control<Button>("CheckUpdates"), "检查更新 · {0}", InAppUpdater.CurrentAppVersion);
            if (!checkingUpdates && !downloadingUpdate) {
                if (availableUpdate is { Ok: true, HasUpdate: true } u) {
                    WinUiLanguage.Message(Control<Button>("ApplyUpdate"), "立即下载并更新 · {0}", u.RemoteVersion);
                    WinUiLanguage.Message(Control<TextBlock>("UpdateStatus"), "发现新版本 {0}，可直接点击上方按钮下载校验并覆盖升级。", u.RemoteVersion);
                } else {
                    WinUiLanguage.SetText(Control<TextBlock>("UpdateStatus"), "更新来源：Flourishze/AirStereo 正式 Release（含 SHA-256 校验）。");
                }
            }
        }
    }

    async Task CheckForUpdatesAsync()
    {
        if (checkingUpdates || downloadingUpdate || closed) return;
        checkingUpdates = true;
        var checkBtn = Control<Button>("CheckUpdates");
        var applyBtn = Control<Button>("ApplyUpdate");
        var status = Control<TextBlock>("UpdateStatus");
        checkBtn.IsEnabled = false;
        WinUiLanguage.SetText(status, "正在检查 GitHub 正式版本…");
        try {
            var res = await InAppUpdater.CheckLatestAsync();
            if (closed) return;
            if (res.NoRelease) {
                availableUpdate = null;
                applyBtn.Visibility = Visibility.Collapsed;
                releaseTargetUrl = InAppUpdater.ReleasesPageUrl;
                WinUiLanguage.SetText(status, "仓库尚无可用的正式 Release");
                return;
            }
            if (!res.Ok) {
                if (!string.IsNullOrWhiteSpace(res.Error)) UiPreferences.Record("update-check: " + res.Error);
                WinUiLanguage.SetText(status, "检查更新失败，请检查网络或稍后重试（并非已是最新版）。");
                return;
            }
            releaseTargetUrl = res.ReleaseUrl;
            if (res.HasUpdate) {
                availableUpdate = res;
                applyBtn.Visibility = Visibility.Visible;
                WinUiLanguage.Message(applyBtn, "立即下载并更新 · {0}", res.RemoteVersion);
                WinUiLanguage.Message(status, "发现新版本 {0}，可直接点击上方按钮下载校验并覆盖升级。", res.RemoteVersion);
            } else {
                availableUpdate = null;
                applyBtn.Visibility = Visibility.Collapsed;
                WinUiLanguage.Message(status, "当前 {0}，远程 {1}，无需更新。", InAppUpdater.CurrentAppVersion, res.RemoteVersion);
            }
        } finally {
            checkingUpdates = false;
            if (!closed) checkBtn.IsEnabled = true;
        }
    }

    async Task DownloadOrCancelUpdateAsync()
    {
        if (closed || InAppUpdater.IsStoreEdition) return;
        if (downloadingUpdate) {
            try { downloadCts?.Cancel(); } catch { }
            return;
        }
        if (availableUpdate is not { Ok: true, HasUpdate: true } target) return;

        downloadingUpdate = true;
        downloadCts = new CancellationTokenSource();
        var checkBtn = Control<Button>("CheckUpdates");
        var applyBtn = Control<Button>("ApplyUpdate");
        var progressPanel = Control<StackPanel>("UpdateProgressPanel");
        var progressBar = Control<ProgressBar>("UpdateProgressBar");
        var progressText = Control<TextBlock>("UpdateProgressText");
        var status = Control<TextBlock>("UpdateStatus");

        checkBtn.IsEnabled = false;
        WinUiLanguage.SetText(applyBtn, "取消下载");
        progressPanel.Visibility = Visibility.Visible;
        progressBar.Value = 0;
        WinUiLanguage.SetText(progressText, "准备下载…");
        WinUiLanguage.Message(status, "正在从 GitHub 下载 AirStereo2 {0} 安装包…", target.RemoteVersion);

        var reporter = new Progress<UpdateDownloadProgress>(p => {
            if (closed) return;
            progressBar.Value = Math.Clamp(p.Percent, 0, 100);
            if (p.TotalBytes > 0) {
                double gotMb = p.BytesReceived / (1024.0 * 1024.0);
                double totMb = p.TotalBytes / (1024.0 * 1024.0);
                double mbps = p.BytesPerSecond / (1024.0 * 1024.0);
                progressText.Text = $"{p.Stage} {p.Percent:F0}% ({gotMb:F1} / {totMb:F1} MB · {mbps:F1} MB/s)";
            } else {
                progressText.Text = p.Stage;
            }
        });

        try {
            var (ok, msiPath, error) = await InAppUpdater.DownloadAndVerifyMsiAsync(
                target.RemoteTag, target.RemoteVersion, reporter, downloadCts.Token);
            if (closed) return;
            if (!ok) {
                WinUiLanguage.SetText(status, error);
                progressPanel.Visibility = Visibility.Collapsed;
                return;
            }
            WinUiLanguage.Message(status, "AirStereo2 {0} 安装包 SHA-256 校验通过，等待确认安装。", target.RemoteVersion);

            var confirm = new ContentDialog {
                Title = string.Format(WinUiLanguage.T("准备更新到 AirStereo2 {0}"), target.RemoteVersion),
                Content = new TextBlock {
                    Text = WinUiLanguage.T("新版安装包已下载并通过 SHA-256 完整性校验。\n点击“开始安装”将启动安装向导，自动关闭当前实例并覆盖升级到原目录。"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = WinUiLanguage.T("开始安装"),
                CloseButtonText = WinUiLanguage.T("稍后"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = view.XamlRoot
            };
            if (await UiDialogs.Show(confirm) == ContentDialogResult.Primary) {
                InAppUpdater.LaunchMsiUpgrade(msiPath);
                WinUiLanguage.SetText(status, "已启动安装向导，请按提示完成升级。");
            }
        } catch (OperationCanceledException) {
            if (!closed) {
                progressPanel.Visibility = Visibility.Collapsed;
                WinUiLanguage.SetText(status, "已取消下载。");
            }
        } catch (Exception ex) {
            if (!closed) {
                progressPanel.Visibility = Visibility.Collapsed;
                WinUiLanguage.Message(status, "更新失败：{0}", ex.Message);
            }
        } finally {
            downloadingUpdate = false;
            try { downloadCts?.Dispose(); } catch { }
            downloadCts = null;
            if (!closed) {
                checkBtn.IsEnabled = true;
                if (availableUpdate is { Ok: true, HasUpdate: true } u)
                    WinUiLanguage.Message(applyBtn, "立即下载并更新 · {0}", u.RemoteVersion);
            }
        }
    }

    void TryOpenUrl(string url)
    {
        try { InAppUpdater.OpenUrl(string.IsNullOrWhiteSpace(url) ? InAppUpdater.ReleasesPageUrl : url); }
        catch { WinUiLanguage.SetText(Control<TextBlock>("UpdateStatus"), "无法打开目标链接，请稍后重试。"); }
    }

    void RenderDefaultSpeakers()
    {
        var devices = client.Snapshot.List("devices");
        var savedDefaults = client.Snapshot.List("defaultOutputs");
        var entries = new List<(string Id, string StableId, string Label, bool IsDefault, bool IsOffline)>();
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) {
            string id = d.Text("id");
            string key = d.Text("key");
            if (!string.IsNullOrEmpty(key)) liveKeys.Add(key);
            string name = d.Text("Name", WinUiLanguage.T("未知音响"));
            string ip = d.Text("IPAddress");
            string label = string.IsNullOrEmpty(ip) ? name : $"{name} ({ip})";
            entries.Add((id, key, label, d.Flag("IsDefaultOutput"), false));
        }
        foreach (var s in savedDefaults) {
            string stableId = s.Text("stableId");
            if (string.IsNullOrEmpty(stableId) || liveKeys.Contains(stableId)) continue;
            string name = s.Text("name", WinUiLanguage.T("未知音响"));
            string ip = s.Text("ip");
            string label = string.IsNullOrEmpty(ip)
                ? $"{name} · {WinUiLanguage.T("已保存 · 离线")}"
                : $"{name} ({ip}) · {WinUiLanguage.T("已保存 · 离线")}";
            entries.Add(("", stableId, label, true, true));
        }

        string signature = (WinUiLanguage.English ? "en|" : "zh|") +
            string.Join(";", entries.Select(e => $"{e.Id}|{e.StableId}|{e.Label}|{(e.IsDefault ? 1 : 0)}"));
        if (signature == defaultSpeakersKey) return;
        defaultSpeakersKey = signature;

        var selectedNames = entries.Where(e => e.IsDefault).Select(e => e.Label.Split(" · ")[0]).ToArray();
        var summary = Control<TextBlock>("DefaultSpeakersSummary");
        summary.Text = selectedNames.Length > 0
            ? WinUiLanguage.T("已选默认音响：") + string.Join("、", selectedNames)
            : WinUiLanguage.T("尚未选择默认音响，请在下方勾选启动时要自动连接的音响。");

        var list = Control<StackPanel>("DefaultSpeakersList");
        list.Children.Clear();
        if (entries.Count == 0) {
            list.Children.Add(new TextBlock {
                Text = WinUiLanguage.T("暂未发现可用音响，请确认音响与电脑在同一局域网后点击“刷新列表”。"),
                Style = ShellView.Style("AirStereo2SecondaryTextStyle"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var entry in entries) {
            var box = new CheckBox {
                Content = entry.Label,
                IsChecked = entry.IsDefault,
                MinWidth = 0
            };
            if (entry.IsOffline) {
                string offlineStableId = entry.StableId;
                box.Click += async (_, _) => {
                    if (box.IsChecked != true)
                        await client.Send("removeDefaultOutput", value: offlineStableId);
                };
            } else {
                string deviceId = entry.Id;
                box.Click += async (_, _) => await client.Set("IsDefaultOutput", box.IsChecked == true, deviceId);
            }
            list.Children.Add(box);
        }
    }

    static int ResolveLatencyTier(int streamingModeIndex, int customBufferSize)
    {
        if (streamingModeIndex == 0) return 0; // 自动
        if (streamingModeIndex == 1) return 1; // 标准 · 200 ms
        if (streamingModeIndex == 2) return 3; // 稳定 · 500 ms
        if (streamingModeIndex == 3) {
            int saved = UiPreferences.Read("latencyTier", -1);
            if (saved == 4) return 4;
            if (customBufferSize == 300) return 2; // 均衡 · 300 ms
            return 4; // 自定义
        }
        return 0;
    }

    static string Db(double value) => (value > 0.05 ? "+" : "") + value.ToString("0.0") + " dB";

    void ConfigureEqualizer()
    {
        Toggle("EqualizerEnabled", "EqEnabled");
        Control<Button>("ResetEqualizer").Click += async (_, _) => {
            if (applyingPreset) return;
            await client.Command("ResetEqCommand");
            UiPreferences.Set("eqPreset", AirStereo2EqualizerPresets.Names[0]);
            Refresh();
        };
        var presets = Control<BottomComboBox>("EqualizerPreset");
        bindings.Add(_ => {
            string[] names = AirStereo2EqualizerPresets.Names.Select(WinUiLanguage.T).ToArray();
            string joined = string.Join("\n", names);
            if (joined != presetNames) { presetNames = joined; presets.ItemsSource = names; }
            if (applyingPreset) return;
            double[] gains = client.Snapshot.List("eqBands").Select(band => band.Number("gain")).ToArray();
            int remembered = Array.IndexOf(AirStereo2EqualizerPresets.Names, UiPreferences.Read("eqPreset", ""));
            int selected = remembered == AirStereo2EqualizerPresets.Custom || AirStereo2EqualizerPresets.Matches(remembered, gains)
                ? remembered : AirStereo2EqualizerPresets.Match(gains);
            if (!Editing(presets)) presets.SelectedIndex = selected;
        });
        presets.SelectionChanged += async (_, _) => {
            if (updating || applyingPreset || presets.SelectedIndex < 0) return;
            int index = presets.SelectedIndex;
            if (index == AirStereo2EqualizerPresets.Custom) { UiPreferences.Set("eqPreset", AirStereo2EqualizerPresets.Names[index]); return; }
            applyingPreset = true;
            presets.IsEnabled = false;
            Control<CheckBox>("EqualizerEnabled").IsEnabled = false;
            foreach (var band in eqBands) band.IsEnabled = false;
            bool applied = true;
            try {
                for (int i = 0; i < 10; i++) {
                    if (!await client.Send("eq", index: i, value: AirStereo2EqualizerPresets.Curves[index][i])) { applied = false; break; }
                }
                if (applied) applied = await client.Set("EqEnabled", true);
                UiPreferences.Set("eqPreset", AirStereo2EqualizerPresets.Names[applied ? index : AirStereo2EqualizerPresets.Custom]);
            } finally {
                applyingPreset = false;
                if (!closed) {
                    presets.IsEnabled = true; Control<CheckBox>("EqualizerEnabled").IsEnabled = true;
                    foreach (var band in eqBands) band.IsEnabled = true;
                    Refresh();
                }
            }
        };
        var grid = Control<Grid>("EqualizerBands");
        string[] defaultLabels = { "31 Hz", "62 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz" };
        for (int i = 0; i < 10; i++) {
            int index = i;
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            eqLabels[i] = new TextBlock { Text = defaultLabels[i], Style = ShellView.Style("AirStereo2SecondaryTextStyle"), FontSize = 11, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            eqBands[i] = new Slider { Minimum = -12, Maximum = 12, StepFrequency = 0.5, Orientation = Orientation.Vertical, Width = 34, Height = 138, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            eqValues[i] = new TextBlock { Text = Db(0), Style = ShellView.Style("AirStereo2SecondaryTextStyle"), FontSize = 10, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (element, row) in new (FrameworkElement, int)[] { (eqLabels[i], 0), (eqBands[i], 1), (eqValues[i], 2) }) {
                grid.Children.Add(element); Grid.SetColumn(element, i); Grid.SetRow(element, row);
            }
            eqBands[i].ValueChanged += async (_, _) => {
                eqValues[index].Text = Db(eqBands[index].Value);
                if (!updating && !applyingPreset) {
                    UiPreferences.Set("eqPreset", AirStereo2EqualizerPresets.Names[AirStereo2EqualizerPresets.Custom]);
                    bool previous = updating; updating = true;
                    try { presets.SelectedIndex = AirStereo2EqualizerPresets.Custom; } finally { updating = previous; }
                    await client.Send("eq", index: index, value: eqBands[index].Value);
                }
            };
        }
        bindings.Add(_ => {
            if (applyingPreset) return;
            var items = client.Snapshot.List("eqBands");
            for (int i = 0; i < eqBands.Length && i < items.Length; i++) {
                if (!Editing(eqBands[i])) eqBands[i].Value = items[i].Number("gain");
                eqLabels[i].Text = items[i].Text("name", eqLabels[i].Text);
                eqValues[i].Text = Db(eqBands[i].Value);
            }
        });
    }

    bool CanFollow => !closed && logPointers.Count == 0 && DateTime.UtcNow - lastInteraction >= TimeSpan.FromSeconds(5) && Control<ScrollViewer>("DiagnosticsPage").Visibility == Visibility.Visible;

    void FollowLogs()
    {
        if (!CanFollow || (!followPending && latestLog == renderedLog)) return;
        Control<TextBlock>("RecentActivity").Text = string.IsNullOrWhiteSpace(latestLog) ? WinUiLanguage.T("暂无诊断日志。") : latestLog;
        renderedLog = latestLog;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (CanFollow) { Control<ScrollViewer>("RecentActivityScroll").ChangeView(null, double.MaxValue, null, true); followPending = false; }
        });
    }

    void RenderDiagnostics()
    {
        var s = client.Snapshot.Field("settings");
        Control<TextBlock>("Diagnostics").Text = s.Text("BackendLogPathText");
        string netStatus = s.Text("NetworkCheckStatusText").Trim();
        Control<TextBlock>("NetworkCheckStatus").Text = string.IsNullOrEmpty(netStatus)
            ? WinUiLanguage.T("可检查本机与 AirPlay 音响之间的网络连通性及 Windows 防火墙规则。")
            : netStatus;
        bool canFix = s.Flag("CanFixFirewall") || client.Snapshot.Field("commandAvailable").Flag("FixFirewallCommand");
        Control<Button>("FixFirewall").IsEnabled = canFix;
    }

    static readonly System.Text.RegularExpressions.Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex IsoTimestampRegex = new(
        @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    static string FormatLogTimestamps(string text, bool english)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = AnsiEscapeRegex.Replace(text, "");
        var cst = TimeSpan.FromHours(8);
        return IsoTimestampRegex.Replace(text, m => {
            if (!DateTimeOffset.TryParse(
                m.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dto))
            {
                return m.Value;
            }
            int dot = m.Value.IndexOf('.');
            int fracLen = 0;
            if (dot >= 0) {
                int end = dot + 1;
                while (end < m.Value.Length && char.IsDigit(m.Value[end])) end++;
                fracLen = Math.Min(end - dot - 1, 6);
            }
            string frac = fracLen > 0 ? "." + new string('f', fracLen) : "";
            return english
                ? dto.ToUniversalTime().ToString($"yyyy-MM-dd'T'HH:mm:ss{frac}'Z'", System.Globalization.CultureInfo.InvariantCulture)
                : dto.ToOffset(cst).ToString($"yyyy-MM-dd HH:mm:ss{frac} '+08:00'", System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    void Refresh()
    {
        if (closed || !client.Ready) return;
        updating = true;
        try {
            var settings = client.Snapshot.Field("settings");
            foreach (var bind in bindings) bind(settings);
            latestLog = FormatLogTimestamps(
                string.Join(Environment.NewLine, client.Snapshot.List("logs").Select(e => e.GetString())),
                WinUiLanguage.English);
            UiPreferences.Set("silent", settings.Flag("SilentStartEnabled"));
            if (lastError.Length == 0) Control<TextBlock>("LatestNotice").Text = WinUiLanguage.T("空闲时跟随最新日志；操作时暂停，停止操作 5 秒后恢复。");
            FollowLogs(); RenderDiagnostics();
        } finally { updating = false; }
    }

    const string PackagedStartupTaskId = "AirStereoStartup";
    const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    async Task LoadPackagedStartupAsync(CheckBox startup)
    {
        try {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(PackagedStartupTaskId);
            if (closed) return;
            startup.IsChecked = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        } catch (Exception ex) {
            if (!closed) Error(ex.Message);
        } finally {
            if (!closed) startup.IsEnabled = true;
        }
    }

    async Task TogglePackagedStartupAsync(CheckBox startup)
    {
        bool want = startup.IsChecked == true;
        startup.IsEnabled = false;
        try {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(PackagedStartupTaskId);
            Windows.ApplicationModel.StartupTaskState state;
            if (want) {
                state = await task.RequestEnableAsync();
                if (state == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                    Error(WinUiLanguage.T("启动项已在 Windows 中关闭，请到“设置 → 应用 → 启动”手动启用 AirStereo2。"));
                else if (state == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
                    Error(WinUiLanguage.T("Windows 策略禁止启用启动项。"));
            } else {
                task.Disable();
                state = task.State;
                if (state == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                    Error(WinUiLanguage.T("Windows 策略不允许关闭此启动项。"));
            }
            if (!closed)
                startup.IsChecked = state is Windows.ApplicationModel.StartupTaskState.Enabled
                    or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        } catch (Exception ex) {
            if (!closed) {
                Error(ex.Message);
                await LoadPackagedStartupAsync(startup);
            }
        } finally {
            if (!closed) startup.IsEnabled = true;
        }
    }

    internal static bool StartupEnabled()
    {
        using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunSubKeyPath);
        if (runKey?.GetValue("AirStereo2") is not string cmd || string.IsNullOrWhiteSpace(cmd))
            return false;
        try {
            using var approvedKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath);
            if (approvedKey?.GetValue("AirStereo2") is byte[] state && state.Length > 0) {
                // Odd first byte (e.g. 0x03) means disabled in Windows Task Manager / Settings > Startup Apps.
                if ((state[0] & 1) != 0) return false;
            }
        } catch { }
        return true;
    }

    internal static void SetStartup(bool enabled)
    {
        using var runKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunSubKeyPath);
        if (enabled) {
            runKey.SetValue("AirStereo2", "\"" + Environment.ProcessPath + "\" --tray");
            try {
                using var approvedKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupApprovedRunPath);
                approvedKey.SetValue("AirStereo2", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
            } catch { }
        } else {
            runKey.DeleteValue("AirStereo2", false);
        }
    }
}
