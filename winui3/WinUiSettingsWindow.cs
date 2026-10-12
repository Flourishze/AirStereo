using AirStereo.Audio;
using AirStereo.Ui;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using System.IO;
using System.Diagnostics;
using WinRT.Interop;

namespace AirStereo;

internal sealed class WinUiSettingsWindow : Window
{
    private readonly WinUiController controller;
    private readonly WinUiSettingsView view;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Slider[] bands = new Slider[AudioProfile.BandCount];
    private readonly TextBlock[] bandValues = new TextBlock[AudioProfile.BandCount];
    private static readonly string[] EqualizerPresetNames =
        { "流行", "摇滚", "古典", "低音", "人声", "爵士", "电子", "平坦", "自定义" };
    private static readonly Dictionary<string, double[]> EqualizerPresetCurves = new()
    {
        ["流行"] = new double[] { -1, 1, 3, 3, 1, -1, -1, 1, 2, 2 },
        ["摇滚"] = new double[] { 4, 3, 1, -1, -2, 1, 3, 4, 3, 2 },
        ["古典"] = new double[] { 3, 2, 1, 0, -1, -1, 0, 2, 3, 4 },
        ["低音"] = new double[] { 5, 4, 3, 2, 1, 0, 0, 0, 0, 0 },
        ["人声"] = new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
        ["爵士"] = new double[] { 3, 2, 1, 2, -1, -1, 0, 2, 3, 4 },
        ["电子"] = new double[] { 4, 3, 1, -1, -2, 1, 0, 2, 3, 4 },
        ["平坦"] = new double[AudioProfile.BandCount]
    };
    private bool updating;
    private bool closed;
    private bool positioning;
    private int refreshQueued;
    private int noticeQueued;
    private int activityDirty;
    private string currentPage = "general";
    private long renderedActivity = -1;
    private long renderedFaults = -1;
    private readonly List<Action> detachHandlers = new();
    private readonly DispatcherQueueTimer diagnosticsTimer;
    private nint hwnd;
    private WindowId anchorWindow;
    private readonly WinUiMotion motion = new();
    private NavigationView navigation => view.Navigation;
    private ScrollViewer generalPage => view.GeneralPageView;
    private ScrollViewer audioPage => view.AudioPageView;
    private Grid recentPage => view.RecentPageView;
    private ScrollViewer diagnosticsPage => view.DiagnosticsPageView;
    private BottomComboBox latency => view.LatencyBox;
    private BottomComboBox appearance => view.AppearanceBox;
    private BottomComboBox language => view.LanguageBox;
    private TextBlock appearanceStatus => view.AppearanceStatusText;
    private Slider customLatency => view.CustomLatencySlider;
    private TextBlock customLatencyValue => view.CustomLatencyText;
    private BottomComboBox codec => view.CodecBox;
    private BottomComboBox rate => view.RateBox;
    private CheckBox reconnect => view.ReconnectCheckBox;
    private NumberBox reconnectAttempts => view.ReconnectAttemptsBox;
    private CheckBox startup => view.StartupCheckBox;
    private CheckBox autoConnect => view.AutoConnectCheckBox;
    private CheckBox silentStartup => view.SilentStartupCheckBox;
    private CheckBox muteLocal => view.MuteLocalCheckBox;
    private CheckBox equalizerEnabled => view.EqualizerEnabledCheckBox;
    private Grid equalizerBands => view.EqualizerBandPanel;
    private Slider balance => view.BalanceSlider;
    private TextBlock balanceValue => view.BalanceText;
    private BottomComboBox equalizerPreset => view.EqualizerPresetBox;
    private TextBlock diagnostics => view.DiagnosticsText;
    private TextBlock latestNotice => view.LatestNoticeText;
    private TextBlock recentActivity => view.RecentActivityText;
    private Button updateButton => view.UpdateButton;
    private Button updateDownload => view.UpdateDownloadButton;
    private TextBlock updateStatus => view.UpdateStatusText;
    private string updatePage = "";
    private CancellationTokenSource? updateCancellation;

    internal WinUiSettingsWindow(WinUiController controller)
    {
        this.controller = controller;
        diagnosticsTimer = dispatcher.CreateTimer();
        diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(200);
        diagnosticsTimer.IsRepeating = false;
        diagnosticsTimer.Tick += OnDiagnosticsTick;
        Title = WinUiLanguage.T("AirStereo · 设置");
        view = new WinUiSettingsView();
        // Resolve the persisted appearance before attaching the root to the
        // Window so the title bar and first XAML pass agree on the theme.
        WinUiTheme.Apply(view);
        Content = view;
        WinUiTheme.Changed += OnThemeChanged;
        WinUiLanguage.Changed += OnLanguageChanged;
        SystemBackdrop = new MicaBackdrop();
        hwnd = WindowNative.GetWindowHandle(this);
        ApplyTitleBarTheme();
        ApplyWindowTheme();
        // The opening size is applied in OpenCentered/PositionCentered. Keep
        // the normal OverlappedPresenter resize behavior so the user can drag
        // the window edges afterward.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsResizable = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AirStereo.ico"));
        // A newly constructed WinUI window can briefly expose its default
        // light non-client surface before Activate() is called. Keep the
        // settings window hidden until its persisted theme and title-bar
        // colors have both been applied.
        WinUiNative.ShowWindow(hwnd, 0); // SW_HIDE
        controller.Changed += Refresh;
        controller.Notice += OnNotice;
        controller.FaultsChanged += QueueDiagnosticsRefresh;
        ConfigureControls();
        Track<Windows.Foundation.TypedEventHandler<object, WindowActivatedEventArgs>>((_, _) => Refresh(), h => Activated += h, h => Activated -= h);
        Track<Windows.Foundation.TypedEventHandler<FrameworkElement, object>>((_, _) => ApplyWindowTheme(), h => view.ActualThemeChanged += h, h => view.ActualThemeChanged -= h);
        // Keep the screenshot-sized dimensions for the first open, but leave
        // the normal title-bar resize behavior available to the user.
        Track<RoutedEventHandler>((_, _) =>
        {
            ApplyTitleBarTheme();
            ApplyWindowTheme();
            motion.Enter(view.Root, 6);
        }, h => view.Root.Loaded += h, h => view.Root.Loaded -= h);
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (closed) return;
        closed = true;
        Closed -= OnClosed;
        controller.Changed -= Refresh;
        controller.Notice -= OnNotice;
        controller.FaultsChanged -= QueueDiagnosticsRefresh;
        WinUiTheme.Changed -= OnThemeChanged;
        WinUiLanguage.Changed -= OnLanguageChanged;
        diagnosticsTimer.Stop();
        diagnosticsTimer.Tick -= OnDiagnosticsTick;
        updateCancellation?.Cancel();
        foreach (Action detach in detachHandlers) detach();
        detachHandlers.Clear();
        foreach (BottomComboBox selector in new[] { latency, appearance, language, codec, rate, equalizerPreset }) selector.Dispose();
        motion.Dispose();
        SystemBackdrop = null;
        Content = null;
        // Drop diagnostic strings and the native event sources with the window.
        recentActivity.Text = diagnostics.Text = latestNotice.Text = "";
        equalizerBands.Children.Clear();
        Array.Clear(bands);
        Array.Clear(bandValues);
    }

    private void Track<T>(T handler, Action<T> attach, Action<T> detach) where T : Delegate
    {
        attach(handler);
        detachHandlers.Add(() => detach(handler));
    }

    private void ConfigureControls()
    {
        Track<Windows.Foundation.TypedEventHandler<NavigationView, NavigationViewSelectionChangedEventArgs>>((_, args) => ShowPage(args.SelectedItem as NavigationViewItem), h => navigation.SelectionChanged += h, h => navigation.SelectionChanged -= h);
        ApplyLanguage();
        Track<EventHandler>((_, _) =>
        {
            if (updating || language.SelectedIndex < 0) return;
            try { WinUiLanguage.Set(language.SelectedIndex == 1); }
            catch (Exception error)
            {
                WinUiLanguage.Message(view.LanguageStatusText, "无法保存语言设置：{0}", error.Message);
                RefreshUi();
            }
        }, h => language.SelectionChanged += h, h => language.SelectionChanged -= h);

        Track<EventHandler>((_, _) => ChangeAppearance(), h => appearance.SelectionChanged += h, h => appearance.SelectionChanged -= h);
        Track<RoutedEventHandler>((_, _) => CheckForUpdates(), h => updateButton.Click += h, h => updateButton.Click -= h);
        Track<RoutedEventHandler>((_, _) => OpenUpdateTarget(), h => updateDownload.Click += h, h => updateDownload.Click -= h);
        WinUiLanguage.Message(updateButton, PackageEnvironment.IsPackaged ? "商店更新 · {0}" : "检查更新 · {0}", VersionInfo.Current);
        WinUiLanguage.SetText(updateDownload, PackageEnvironment.IsPackaged ? "打开项目说明" : "查看更新与下载");
        updateDownload.IsEnabled = PackageEnvironment.IsPackaged;
        WinUiLanguage.SetText(updateStatus, PackageEnvironment.IsPackaged
            ? "商店版由 Microsoft Store 管理更新。"
            : "更新来源：Flourishze/AirStereo 正式 Release。");

        Track<EventHandler>((_, _) => ChangeLatency(), h => latency.SelectionChanged += h, h => latency.SelectionChanged -= h);
        Track<Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventHandler>((_, e) =>
        {
            customLatencyValue.Text = ((int)Math.Round(e.NewValue)) + " ms";
            if (!updating && latency.SelectedIndex == 4)
                controller.SetLatency(LatencyMode.Custom, (int)Math.Round(e.NewValue));
        }, h => customLatency.ValueChanged += h, h => customLatency.ValueChanged -= h);
        Track<EventHandler>((_, _) =>
        {
            if (!updating && codec.SelectedIndex >= 0)
                controller.SetFormat(codec.SelectedIndex == 0, controller.SampleRate);
        }, h => codec.SelectionChanged += h, h => codec.SelectionChanged -= h);
        Track<EventHandler>((_, _) =>
        {
            if (!updating && rate.SelectedIndex >= 0)
                controller.SetFormat(controller.UseAlac, rate.SelectedIndex == 1 ? 48000 : 44100);
        }, h => rate.SelectionChanged += h, h => rate.SelectionChanged -= h);
        Track<RoutedEventHandler>((_, _) => ChangeReconnect(), h => reconnect.Checked += h, h => reconnect.Checked -= h);
        Track<RoutedEventHandler>((_, _) => ChangeReconnect(), h => reconnect.Unchecked += h, h => reconnect.Unchecked -= h);
        Track<Windows.Foundation.TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>>((_, _) =>
        {
            if (!updating && !double.IsNaN(reconnectAttempts.Value))
                controller.SetAutoReconnect(reconnect.IsChecked == true, ReconnectAttemptValue());
        }, h => reconnectAttempts.ValueChanged += h, h => reconnectAttempts.ValueChanged -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetStartup(true); }, h => startup.Checked += h, h => startup.Checked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetStartup(false); }, h => startup.Unchecked += h, h => startup.Unchecked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetSilentStartup(true); }, h => silentStartup.Checked += h, h => silentStartup.Checked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetSilentStartup(false); }, h => silentStartup.Unchecked += h, h => silentStartup.Unchecked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetAutoConnect(true); }, h => autoConnect.Checked += h, h => autoConnect.Checked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetAutoConnect(false); }, h => autoConnect.Unchecked += h, h => autoConnect.Unchecked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetMuteLocalOutput(true); }, h => muteLocal.Checked += h, h => muteLocal.Checked -= h);
        Track<RoutedEventHandler>((_, _) => { if (!updating) controller.SetMuteLocalOutput(false); }, h => muteLocal.Unchecked += h, h => muteLocal.Unchecked -= h);
        Track<RoutedEventHandler>((_, _) => PublishCalibration(), h => equalizerEnabled.Checked += h, h => equalizerEnabled.Checked -= h);
        Track<RoutedEventHandler>((_, _) => PublishCalibration(), h => equalizerEnabled.Unchecked += h, h => equalizerEnabled.Unchecked -= h);
        Track<EventHandler>((_, _) => ApplyEqualizerPreset(), h => equalizerPreset.SelectionChanged += h, h => equalizerPreset.SelectionChanged -= h);
        Track<Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventHandler>((_, e) =>
        {
            if (updating) return;
            int value = (int)Math.Round(e.NewValue);
            controller.SetBalance(value);
            balanceValue.Text = BalanceText(value);
        }, h => balance.ValueChanged += h, h => balance.ValueChanged -= h);
        Track<RoutedEventHandler>((_, _) => controller.OpenDiagnosticsDirectory(), h => view.OpenDiagnosticsButton.Click += h, h => view.OpenDiagnosticsButton.Click -= h);
        Track<RoutedEventHandler>((_, _) => RefreshDiagnostics(force: true), h => view.ReloadDiagnosticsButton.Click += h, h => view.ReloadDiagnosticsButton.Click -= h);
        BuildEqualizerBands();
        navigation.SelectedItem = navigation.MenuItems[0];
        RefreshUi();
    }

    private void ShowPage(NavigationViewItem? item)
    {
        string tag = item?.Tag as string ?? "general";
        currentPage = tag;
        diagnosticsTimer.Stop();
        generalPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        audioPage.Visibility = tag == "audio" ? Visibility.Visible : Visibility.Collapsed;
        recentPage.Visibility = tag == "notice" ? Visibility.Visible : Visibility.Collapsed;
        diagnosticsPage.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement page = tag switch
        {
            "audio" => audioPage,
            "notice" => recentPage,
            "diagnostics" => diagnosticsPage,
            _ => generalPage
        };
        motion.Enter(page, 6);
        RefreshDiagnostics(force: true);
    }

    private void BuildEqualizerBands()
    {
        AudioProfile profile = controller.CalibrationProfile;
        equalizerBands.ColumnDefinitions.Clear();
        equalizerBands.Children.Clear();
        for (int i = 0; i < AudioProfile.BandCount; i++)
        {
            equalizerBands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string labelText = FormatFrequency(AudioProfile.Frequency(i));
            TextBlock label = new()
            {
                Text = labelText,
                Style = Resource<Style>("AirStereoSecondaryTextStyle"),
                FontSize = 11,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 0);
            equalizerBands.Children.Add(label);

            Slider slider = new()
            {
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 1,
                Orientation = Orientation.Vertical,
                Width = 34,
                Height = 138,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Value = profile.BandGainDb(i)
            };
            int band = i;
            Track<Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventHandler>((_, _) =>
            {
                bandValues[band].Text = Db(slider.Value);
                if (updating) return;
                int customIndex = Array.IndexOf(EqualizerPresetNames, "自定义");
                if (equalizerPreset.SelectedIndex != customIndex)
                {
                    updating = true;
                    try { equalizerPreset.SelectedIndex = customIndex; }
                    finally { updating = false; }
                }
                PublishCalibration();
            }, h => slider.ValueChanged += h, h => slider.ValueChanged -= h);
            bands[i] = slider;
            Grid.SetColumn(slider, i);
            Grid.SetRow(slider, 1);
            equalizerBands.Children.Add(slider);

            TextBlock value = new()
            {
                Text = Db(slider.Value),
                Style = Resource<Style>("AirStereoSecondaryTextStyle"),
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            bandValues[i] = value;
            Grid.SetColumn(value, i);
            Grid.SetRow(value, 2);
            equalizerBands.Children.Add(value);
        }
    }

    private void PublishCalibration()
    {
        if (updating || bands.Any(item => item == null)) return;
        string preset = equalizerPreset.SelectedIndex >= 0 && equalizerPreset.SelectedIndex < EqualizerPresetNames.Length
            ? EqualizerPresetNames[equalizerPreset.SelectedIndex] : "自定义";
        controller.SetCalibration(equalizerEnabled.IsChecked == true, bands.Select(item => item.Value).ToArray(), preset);
    }

    private void ApplyEqualizerPreset()
    {
        if (updating || equalizerPreset.SelectedIndex < 0 || equalizerPreset.SelectedIndex >= EqualizerPresetNames.Length) return;
        string name = EqualizerPresetNames[equalizerPreset.SelectedIndex];
        if (!EqualizerPresetCurves.TryGetValue(name, out double[]? curve))
        {
            controller.SetCalibration(equalizerEnabled.IsChecked == true, bands.Select(item => item.Value).ToArray(), name);
            return;
        }

        updating = true;
        try
        {
            equalizerEnabled.IsChecked = true;
            for (int i = 0; i < bands.Length; i++)
            {
                bands[i].Value = curve[i];
                bandValues[i].Text = Db(curve[i]);
            }
        }
        finally { updating = false; }
        controller.SetCalibration(true, curve, name);
    }

    private static string FormatFrequency(double frequency) => frequency >= 1000
        ? (frequency % 1000 == 0 ? (frequency / 1000).ToString("0") : (frequency / 1000).ToString("0.#")) + " kHz"
        : frequency.ToString("0") + " Hz";

    private void ChangeLatency()
    {
        if (updating || latency.SelectedIndex < 0) return;
        LatencyMode mode = (LatencyMode)latency.SelectedIndex;
        controller.SetLatency(mode, (int)Math.Round(customLatency.Value));
        customLatency.IsEnabled = mode == LatencyMode.Custom;
    }

    private void ChangeAppearance()
    {
        if (updating || appearance.SelectedIndex < 0) return;
        try
        {
            WinUiTheme.Set((WinUiAppearanceMode)appearance.SelectedIndex);
            WinUiLanguage.SetText(appearanceStatus, "外观已应用并保存。");
        }
        catch (Exception error) { WinUiLanguage.Message(appearanceStatus, "外观已应用但无法保存：{0}", error.Message); }
    }

    private async void CheckForUpdates()
    {
        if (closed) return;
        if (PackageEnvironment.IsPackaged)
        {
            OpenUrl(PackageEnvironment.StoreUpdatesUri, "无法打开 Microsoft Store，请在商店的下载页面检查更新");
            return;
        }

        updateButton.IsEnabled = false;
        updateDownload.IsEnabled = false;
        using var lifetime = new CancellationTokenSource();
        updateCancellation = lifetime;
        WinUiLanguage.SetText(updateStatus, "正在检查 GitHub 正式版本…");
        try
        {
            // Detach this window's await promptly on close. The shared update
            // service and its existing request timeout remain unchanged.
            UpdateResult result = await UpdateService.CheckAsync().WaitAsync(lifetime.Token);
            if (closed) return;
            updatePage = result.Page ?? "";
            if (result.NoRelease) WinUiLanguage.Message(updateStatus, "仓库尚无可用的正式 Release");
            else if (result.Available) WinUiLanguage.Message(updateStatus, "发现新版本 {0}，请查看发布说明并下载安装包。", result.Version);
            else WinUiLanguage.Message(updateStatus, "当前 {0}，远程 {1}，无需更新。", VersionInfo.Current, result.Version);
            updateDownload.IsEnabled = result.Available;
        }
        catch (Exception)
        {
            if (!closed) WinUiLanguage.Message(updateStatus, "检查更新失败，请检查网络或稍后重试（并非已是最新版）。");
        }
        finally
        {
            if (ReferenceEquals(updateCancellation, lifetime)) updateCancellation = null;
            if (!closed) updateButton.IsEnabled = true;
        }
    }

    private void OpenUpdateTarget()
    {
        string target = PackageEnvironment.IsPackaged ? VersionInfo.Repository : updatePage;
        if (string.IsNullOrWhiteSpace(target)) return;
        OpenUrl(target, "无法打开浏览器，请稍后重试");
    }

    private void OpenUrl(string target, string failure)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { WinUiLanguage.Message(updateStatus, failure); }
    }

    private void ChangeReconnect()
    {
        if (!updating)
                controller.SetAutoReconnect(reconnect.IsChecked == true, ReconnectAttemptValue());
    }

    private void Refresh()
    {
        if (closed || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            RefreshUi();
        })) Interlocked.Exchange(ref refreshQueued, 0);
    }

    private void RefreshUi()
    {
        if (closed) return;
        updating = true;
        try
        {
            language.SelectedIndex = WinUiLanguage.English ? 1 : 0;
            latency.SelectedIndex = (int)controller.LatencyMode;
            appearance.SelectedIndex = (int)WinUiTheme.Mode;
            customLatency.Value = controller.CustomLatencyMs;
            view.SetConnectionControlsEnabled(!controller.Playing, controller.LatencyMode == LatencyMode.Custom);
            customLatencyValue.Text = controller.CustomLatencyMs + " ms";
            codec.SelectedIndex = controller.UseAlac ? 0 : 1;
            rate.SelectedIndex = controller.SampleRate == 48000 ? 1 : 0;
            reconnect.IsChecked = controller.PlaybackAutoReconnect;
            reconnectAttempts.Value = Math.Clamp(controller.MaxPlaybackReconnectAttempts, 1, 10);
            startup.IsChecked = controller.StartupEnabled;
            autoConnect.IsChecked = controller.AutoConnect;
            silentStartup.IsChecked = controller.SilentStartup;
            muteLocal.IsChecked = controller.MuteLocalOutput;
            AudioProfile profile = controller.CalibrationProfile;
            equalizerEnabled.IsChecked = profile.Enabled;
            int presetIndex = Array.IndexOf(EqualizerPresetNames, controller.EqualizerPresetName);
            equalizerPreset.SelectedIndex = presetIndex >= 0 ? presetIndex : Array.IndexOf(EqualizerPresetNames, "自定义");
            for (int i = 0; i < bands.Length; i++)
            {
                bands[i].Value = profile.BandGainDb(i);
                bandValues[i].Text = Db(bands[i].Value);
            }
            balance.Value = controller.Balance;
            balance.IsEnabled = controller.HasBalance && !controller.Scanning;
            balanceValue.Text = BalanceText(controller.Balance);
            QueueDiagnosticsRefresh();
        }
        finally { updating = false; }
    }

    private int ReconnectAttemptValue()
    {
        if (double.IsNaN(reconnectAttempts.Value)) return 1;
        return Math.Clamp((int)Math.Round(reconnectAttempts.Value), 1, 10);
    }

    private void RefreshDiagnostics(bool force = false)
    {
        if (closed) return;
        if (currentPage == "notice" && (force || renderedActivity != controller.ActivityRevision))
        {
            renderedActivity = controller.ActivityRevision;
            latestNotice.Text = string.IsNullOrWhiteSpace(controller.LastNotice) ? WinUiLanguage.T("暂无最近提示。") : WinUiLanguage.Status(controller.LastNotice);
            IReadOnlyList<string> activity = controller.RecentActivity;
            recentActivity.Text = activity.Count == 0 ? WinUiLanguage.T("暂无诊断日志。") : string.Join(Environment.NewLine, activity);
        }
        if (currentPage == "diagnostics" && (force || renderedFaults != controller.FaultRevision))
        {
            renderedFaults = controller.FaultRevision;
            List<FaultEntry> faults = controller.ReadFaults();
            diagnostics.Text = faults.Count == 0
                ? string.Format(WinUiLanguage.T("暂无故障记录。\n\n故障文件目录：{0}"), controller.DiagnosticsDirectory)
                : string.Join("\n\n", faults.Take(12).Select(item => item.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + WinUiLanguage.T(item.Category) + "\n" + item.Message));
        }
    }

    private void OnDiagnosticsTick(DispatcherQueueTimer sender, object args) => RefreshDiagnostics();
    private void OnNotice(string _)
    {
        Interlocked.Exchange(ref activityDirty, 1);
        QueueDiagnosticsRefresh();
    }
    private void QueueDiagnosticsRefresh()
    {
        if (closed) return;
        if (!dispatcher.HasThreadAccess)
        {
            if (Interlocked.Exchange(ref noticeQueued, 1) != 0) return;
            if (!dispatcher.TryEnqueue(() => { Interlocked.Exchange(ref noticeQueued, 0); QueueDiagnosticsRefresh(); }))
                Interlocked.Exchange(ref noticeQueued, 0);
            return;
        }
        if (Interlocked.Exchange(ref activityDirty, 0) != 0) renderedActivity = -1;
        if (currentPage != "notice" && currentPage != "diagnostics") return;
        if (!diagnosticsTimer.IsRunning) diagnosticsTimer.Start();
    }

    private void OnThemeChanged(WinUiAppearanceMode _)
    {
        if (closed) return;
        void Apply()
        {
            if (closed) return;
            WinUiTheme.Apply(view);
            ApplyWindowTheme();
        }
        if (dispatcher.HasThreadAccess) Apply();
        else dispatcher.TryEnqueue(Apply);
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
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
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("设置标题栏主题失败：" + error.Message);
        }
    }

    private void ApplyWindowTheme()
    {
        bool dark = WinUiTheme.IsDark(view);
        WinUiNative.ApplyWindowTheme(hwnd, dark);
        ApplyTitleBarTheme();
    }

    internal void OpenCentered(WindowId anchor)
    {
        if (closed) return;
        anchorWindow = anchor;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        // Apply the selected appearance before the first visible frame. This
        // avoids a light title bar/background flash when dark mode is selected.
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
        // Move to the tray's monitor before showing; repeat after layout obtains
        // that monitor's DPI so both centering and size use its work area.
        PositionCentered();
        AppWindow.Show();
        Activate();
        dispatcher.TryEnqueue(() => { if (!closed) { PositionCentered(); motion.Enter(view.Root, 6); } });
    }

    private void PositionCentered()
    {
        if (closed || positioning) return;
        positioning = true;
        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(anchorWindow, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;
            double scale = view.Root.XamlRoot?.RasterizationScale ?? 1;
            // Screenshot-sized baseline, scaled for DPI and capped on small
            // screens. Existing page scroll viewers keep every setting reachable.
            int margin = (int)Math.Round(16 * scale);
            int width = Math.Min((int)Math.Round(950 * scale), Math.Max(1, work.Width - 2 * margin));
            int height = Math.Min((int)Math.Round(745 * scale), Math.Max(1, work.Height - 2 * margin));
            var bounds = new RectInt32(work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2, width, height);
            if (AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y ||
                AppWindow.Size.Width != width || AppWindow.Size.Height != height)
                AppWindow.MoveAndResize(bounds);
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine("设置窗口居中失败：" + error.Message); }
        finally { positioning = false; }
    }

    private static T? Resource<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out object value) ? value as T : null;

    private static string Db(double value) => (value > 0.05 ? "+" : "") + value.ToString("0.0") + " dB";
    private static string BalanceText(int value) => value == 0 ? WinUiLanguage.T("居中") : (value > 0 ? "R -" + value : "L -" + Math.Abs(value));

    private void ApplyLanguage()
    {
        bool wasUpdating = updating;
        updating = true;
        try
        {
            view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
            Title = WinUiLanguage.T("AirStereo · 设置");
            navigation.OpenPaneLength = WinUiLanguage.English ? 192 : 148;
            language.ItemsSource = new[] { "中文（简体）", "English" };
            language.SelectedIndex = WinUiLanguage.English ? 1 : 0;
            appearance.ItemsSource = new[] { "跟随系统", "浅色", "深色" }.Select(WinUiLanguage.T).ToArray();
            latency.ItemsSource = new[] { "实时 · 120 ms", "标准 · 200 ms", "缓冲 · 500 ms", "稳定 · 1000 ms", "自定义" }.Select(WinUiLanguage.T).ToArray();
            codec.ItemsSource = new[] { "ALAC·无损压缩", "PCM·未压缩" }.Select(WinUiLanguage.T).ToArray();
            rate.ItemsSource = new[] { "44.1 kHz · 音乐默认", "48 kHz · 视频/游戏" }.Select(WinUiLanguage.T).ToArray();
            // Preset indices and saved curve identifiers stay language-neutral.
            equalizerPreset.ItemsSource = EqualizerPresetNames.Select(WinUiLanguage.T).ToArray();
        }
        finally { updating = wasUpdating; }
    }

    private void OnLanguageChanged()
    {
        dispatcher.TryEnqueue(() =>
        {
            if (closed) return;
            ApplyLanguage();
            RefreshUi();
            RefreshDiagnostics(force: true);
        });
    }
}
