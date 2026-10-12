using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using WinRT.Interop;
using System.IO;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CheckBox = Microsoft.UI.Xaml.Controls.CheckBox;

namespace AirStereo;

internal sealed class WinUiMainWindow : Window
{
    private readonly WinUiController controller;
    private readonly WinUiMainView view;
    private TextBlock status => view.StatusText;
    private StackPanel deviceList => view.DeviceList;
    private Button playButton => view.PlayButton;
    private FontIcon playIcon => view.PlayIconControl;
    private TextBlock playText => view.PlayTextControl;
    private Slider volumeSlider => view.VolumeSlider;
    private CheckBox realtimeVolume => view.RealtimeVolumeCheckBox;
    private TextBlock volumeValue => view.VolumeText;
    private DispatcherQueueTimer dismiss = null!;
    private DispatcherQueueTimer localMuteTimer = null!;
    private nint hwnd;
    private bool rebuilding;
    private bool closing;
    private bool updating;
    private bool popupVisible;
    private bool startupScanStarted;
    private int refreshQueued;
    private readonly Dictionary<string, DeviceRowState> deviceRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock emptyDevices = Text("", 13, false);
    private int visibilityVersion;
    private readonly WinUiMotion motion = new();
    private WinUiTrayIcon? tray;
    private WinUiSettingsWindow? settings;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    internal WinUiMainWindow(bool startInTray, int silenceDisconnectMilliseconds, bool useAlac,
        Session.SilenceFrameMode silenceMode, WinUiController? isolatedController = null)
    {
        Title = "AirStereo · 立体声 AirPlay 发送器";
        controller = isolatedController ?? new WinUiController(Post, silenceDisconnectMilliseconds, useAlac, silenceMode);
        controller.Changed += Refresh;

        view = new WinUiMainView();
        view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
        // Set RequestedTheme before the root is attached to the Window. This
        // prevents the first XAML measure/render pass from using the default
        // light resources when the saved appearance is dark.
        WinUiTheme.Apply(view);
        Content = view;
        WinUiTheme.Changed += OnThemeChanged;
        WinUiLanguage.Changed += OnLanguageChanged;
        // Do not use Acrylic on this borderless tray surface. WinUI/DWM can
        // compose one light backdrop frame before the requested dark material
        // is ready. The root already has the Fluent theme background, so using
        // it directly is both deterministic and avoids the white flash.
        hwnd = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var exStyle = WinUiNative.GetWindowLongPtrW(hwnd, -20).ToInt64();
        WinUiNative.SetWindowLongPtrW(hwnd, -20, (nint)((exStyle & ~0x40000L) | 0x80));
        int corners = 2;
        WinUiNative.DwmSetWindowAttribute(hwnd, 33, ref corners, 4);
        // SetBorderAndTitleBar(false, false) removes the caption, but the
        // unpackaged presenter can still leave a one-pixel non-client frame.
        // Convert this tray surface to a true popup so DWM cannot paint a
        // white frame around the dark content.
        WinUiNative.RemoveNonClientFrame(hwnd);
        // Set the DWM attributes after removing the non-client title bar.
        // SetBorderAndTitleBar can otherwise restore the default light frame.
        ApplyWindowTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AirStereo.ico"));
        dismiss = dispatcher.CreateTimer();
        dismiss.Interval = TimeSpan.FromMilliseconds(170);
        dismiss.IsRepeating = false;
        dismiss.Tick += (_, _) =>
        {
            nint foreground = WinUiNative.GetForegroundWindow();
            if (ShouldDismissPopup(popupVisible, closing, settings != null, tray?.MenuOpen == true,
                hwnd, foreground, foreground == 0 ? 0 : WinUiNative.GetAncestor(foreground, 3)))
                HideWindow();
        };
        localMuteTimer = dispatcher.CreateTimer();
        localMuteTimer.Interval = TimeSpan.FromMilliseconds(500);
        localMuteTimer.IsRepeating = true;
        localMuteTimer.Tick += (_, _) =>
        {
            if (!closing)
            {
                controller.MaintainLocalMute();
                if (popupVisible) UpdateScanVisuals();
                // Settings or a native menu can consume the only deactivation
                // event. Recheck while visible rather than requiring another
                // activation/deactivation cycle to restore auto-dismiss.
                if (popupVisible) ScheduleDismissIfOutside();
            }
        };
        localMuteTimer.Start();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && settings == null && tray?.MenuOpen != true)
                dismiss.Start();
            else
                dismiss.Stop();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (!closing)
            {
                args.Cancel = true;
                HideWindow();
            }
        };
        try
        {
            string icon = TrayIconPath();
            tray = new WinUiTrayIcon(icon);
            tray.Toggle += () => { dismiss.Stop(); ToggleVisibility(); };
            tray.Open += Open;
            tray.Settings += OpenSettings;
            tray.StopPlayback += controller.StopPlayback;
            tray.Exit += ExitApplication;
        }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine("托盘初始化失败：" + error.Message); }

        Activated += (_, _) => Refresh();
        view.ActualThemeChanged += (_, _) => { ApplyWindowTheme(); Refresh(); };
        volumeSlider.Value = controller.Volume;
        volumeValue.Text = controller.Volume + "%";
        volumeSlider.ValueChanged += VolumeChanged;
        realtimeVolume.IsChecked = controller.RealtimeVolume;
        realtimeVolume.Checked += (_, _) => controller.SetRealtimeVolume(true);
        realtimeVolume.Unchecked += (_, _) => controller.SetRealtimeVolume(false);
        view.RefreshButtonControl.Click += (_, _) => { controller.StartScan(); Refresh(); };
        view.SettingsButton.Click += (_, _) => OpenSettings();
        view.HideButton.Click += (_, _) => HideWindow();
        playButton.Click += (_, _) => { if (controller.Playing) controller.StopPlayback(); else controller.StartPlayback("loopback"); };
        view.ApplyVolumeButton.Click += (_, _) => { motion.Pulse(view.ApplyVolumeIconControl); controller.ApplyVolume(); };
        view.Root.Loaded += (_, _) =>
        {
            // The requested theme and DWM theme are normally applied before the
            // window is shown. Keep this as a final sync for the first XAML pass,
            // but don't change the theme after a visible frame has been painted.
            ApplyWindowTheme();
            if (popupVisible && !closing)
            {
                motion.Enter(view.Root);
                UpdateScanVisuals();
            }
        };
    }

    private static string TrayIconPath()
    {
        bool lightTaskbar = false;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return Path.Combine(AppContext.BaseDirectory, lightTaskbar ? "TrayDark.ico" : "TrayLight.ico");
    }

    private void RebuildDevices()
    {
        if (rebuilding) return;
        rebuilding = true;
        try
        {
            if (controller.Scanning)
            {
                emptyDevices.Text = WinUiLanguage.T("正在扫描局域网中的 AirPlay 音响…");
                if (deviceList.Children.Count != 1 || !ReferenceEquals(deviceList.Children[0], emptyDevices))
                {
                    deviceList.Children.Clear();
                    deviceList.Children.Add(emptyDevices);
                }
                return;
            }
            if (controller.Groups.Count == 0)
            {
                foreach (var row in deviceRows.Values) row.Dispose();
                deviceRows.Clear();
                emptyDevices.Text = WinUiLanguage.T(controller.Scanning ? "正在扫描局域网中的 AirPlay 音响…"
                    : controller.NetworkUnavailable ? "网络不可用，请检查 IPv4 网络后刷新。" : "未发现音响，请确认电脑和音响在同一局域网。");
                if (deviceList.Children.Count != 1 || !ReferenceEquals(deviceList.Children[0], emptyDevices))
                {
                    deviceList.Children.Clear();
                    deviceList.Children.Add(emptyDevices);
                }
                return;
            }
            // Only discovery replaces membership; volume/status changes reuse
            // the cards, including after the scan placeholder has been removed.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < controller.Groups.Count; index++)
            {
                ReceiverGroup group = controller.Groups[index];
                string key = WinUiController.SelectionKey(group);
                if (!seen.Add(key)) { key += "|duplicate:" + index; seen.Add(key); }
                if (!deviceRows.TryGetValue(key, out DeviceRowState? row))
                {
                    row = DeviceRow(group);
                    deviceRows.Add(key, row);
                }
                UpdateDeviceRow(row, group);
                if (index >= deviceList.Children.Count || !ReferenceEquals(deviceList.Children[index], row.Card))
                {
                    deviceList.Children.Remove(row.Card);
                    deviceList.Children.Insert(index, row.Card);
                }
            }
            while (deviceList.Children.Count > controller.Groups.Count) deviceList.Children.RemoveAt(deviceList.Children.Count - 1);
            foreach (string key in deviceRows.Keys.Where(key => !seen.Contains(key)).ToArray())
            {
                deviceRows[key].Dispose();
                deviceRows.Remove(key);
            }
        }
        finally { rebuilding = false; }
    }

    private sealed class DeviceRowState : IDisposable
    {
        internal required Border Card;
        internal required CheckBox Check;
        internal required TextBlock Name;
        internal required TextBlock Online;
        internal required TextBlock Detail;
        internal required TextBlock Role;
        internal ReceiverGroup? Group;
        internal bool English;
        internal bool Selected;
        internal RoutedEventHandler? OnChecked;
        internal RoutedEventHandler? OnUnchecked;
        public void Dispose()
        {
            Check.Checked -= OnChecked;
            Check.Unchecked -= OnUnchecked;
            OnChecked = OnUnchecked = null;
            Group = null;
        }
    }

    private void UpdateDeviceRow(DeviceRowState row, ReceiverGroup group)
    {
        if (!ReferenceEquals(row.Group, group) || row.English != WinUiLanguage.English)
        {
            row.Group = group;
            row.English = WinUiLanguage.English;
            row.Name.Text = DisplayName(group);
            row.Detail.Text = DisplayDetail(group);
            row.Online.Text = WinUiLanguage.T("在线");
        }
        bool selected = controller.IsSelected(group);
        if (row.Check.IsChecked != selected) row.Check.IsChecked = selected;
        if (row.Selected != selected)
        {
            row.Card.Style = Resource<Style>(selected ? "AirStereoSelectedDeviceCardStyle" : "AirStereoDeviceCardStyle");
            row.Selected = selected;
        }
        row.Check.IsEnabled = !controller.Scanning;
        row.Role.Text = WinUiLanguage.T(controller.Role(group));
    }

    private DeviceRowState DeviceRow(ReceiverGroup group)
    {
        Grid grid = new() { ColumnSpacing = 4, Padding = new Thickness(7, 5, 7, 5), MinHeight = 56 };
        // Keep the left controls in fixed columns.  An Auto/Star combination here
        // can let the icon drift toward the center when the popup is widened.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CheckBox check = new()
        {
            IsChecked = controller.IsSelected(group),
            Width = 22,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(check);
        Border speakerIcon = new()
        {
            Style = Resource<Style>("AirStereoSpeakerIconStyle"),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE7F4",
                Style = Resource<Style>("AirStereoAccentIconStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(speakerIcon, 1);
        grid.Children.Add(speakerIcon);
        StackPanel text = new()
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock name = Text(DisplayName(group), 13, true);
        name.FontSize = 14;
        StackPanel detailRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        TextBlock online = new()
        {
            Text = WinUiLanguage.T("在线"),
            Style = Resource<Style>("AirStereoOnlineTextStyle"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        detailRow.Children.Add(online);
        detailRow.Children.Add(new TextBlock
        {
            Text = "·",
            Style = Resource<Style>("AirStereoSecondaryTextStyle"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        TextBlock detail = Text(DisplayDetail(group), 11, false);
        detail.FontSize = 12;
        detail.TextWrapping = TextWrapping.Wrap;
        detailRow.Children.Add(detail);
        text.Children.Add(name); text.Children.Add(detailRow);
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        TextBlock role = new()
        {
            Text = WinUiLanguage.T(controller.Role(group)),
            Style = Resource<Style>("AirStereoRoleTextStyle"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(role, 3); grid.Children.Add(role);
        var card = new Border
        {
            Child = grid,
            Style = Resource<Style>(controller.IsSelected(group)
                ? "AirStereoSelectedDeviceCardStyle"
                : "AirStereoDeviceCardStyle"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var row = new DeviceRowState { Card = card, Check = check, Name = name, Online = online,
            Detail = detail, Role = role, Selected = controller.IsSelected(group) };
        row.OnChecked = (_, _) => { if (row.Group != null) Select(row.Group, true); };
        row.OnUnchecked = (_, _) => { if (row.Group != null) Select(row.Group, false); };
        check.Checked += row.OnChecked;
        check.Unchecked += row.OnUnchecked;
        return row;
    }

    private static string DisplayName(ReceiverGroup group)
    {
        return !string.IsNullOrWhiteSpace(group.Name)
            ? group.Name
            : group.Members.FirstOrDefault()?.Instance ?? WinUiLanguage.T("未知音响");
    }

    private string DisplayDetail(ReceiverGroup group)
    {
        List<string> parts = new();
        if (group.IsStereoPair) parts.Add(string.Format(WinUiLanguage.T("原生立体声对 · {0} 只"), group.Members.Count));
        else if (group.Members.Count > 1) parts.Add(string.Format(WinUiLanguage.T("{0} 只 · 软件立体声"), group.Members.Count));
        else parts.Add(WinUiLanguage.T("独立音响"));
        foreach (Receiver member in group.Members)
        {
            if (!string.IsNullOrWhiteSpace(member.Model) &&
                !member.Model.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase))
                parts.Add(member.Model);
        }
        parts = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(" · ", parts);
    }

    private void Select(ReceiverGroup group, bool selected)
    {
        if (rebuilding) return;
        controller.Select(group, selected);
        Refresh();
    }

    private void VolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (updating) return;
        int value = (int)Math.Round(args.NewValue);
        volumeValue.Text = value + "%";
        controller.SetVolumeValue(value);
        if (controller.RealtimeVolume) controller.ApplyVolume();
    }

    private void Refresh()
    {
        if (closing || (dispatcher.HasThreadAccess && !popupVisible)) return;
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            RefreshOnUiThread();
        })) Interlocked.Exchange(ref refreshQueued, 0);
    }

    private void RefreshOnUiThread()
    {
        if (closing || !popupVisible) return;
        UpdateScanVisuals();
        updating = true;
        status.Text = WinUiLanguage.Status(controller.Status) + " · " + controller.CurrentLatencyMs + " ms";
        playButton.IsEnabled = controller.CanPlay || controller.CanStop;
        playText.Text = WinUiLanguage.T(controller.Playing ? "停止播放" : "播放电脑声音");
        playIcon.Glyph = controller.Playing ? "\uE71A" : "\uE768";
        volumeSlider.Value = controller.Volume;
        volumeValue.Text = controller.Volume + "%";
        realtimeVolume.IsChecked = controller.RealtimeVolume;
        view.ApplyVolumeButton.IsEnabled = controller.CanApplyVolume;
        view.FormatText.Text = (controller.CurrentCodec.Length > 0 ? controller.CurrentCodec : controller.UseAlac ? "ALAC" : "PCM") + " · " + (controller.SampleRate == 48000 ? "48 kHz" : "44.1 kHz");
        RebuildDevices();
        updating = false;
    }

    private void OpenSettings()
    {
        dismiss.Stop();
        if (settings == null)
        {
            var openedSettings = new WinUiSettingsWindow(controller);
            settings = openedSettings;
            Windows.Foundation.TypedEventHandler<object, WindowEventArgs>? onClosed = null;
            onClosed = (_, _) =>
            {
                openedSettings.Closed -= onClosed;
                if (ReferenceEquals(settings, openedSettings)) settings = null;
                // Closing may restore foreground ownership after Closed fires.
                // Inspect on the next dispatcher turn; the dismiss timer then
                // checks again after the normal debounce interval.
                dispatcher.TryEnqueue(ScheduleDismissIfOutside);
            };
            openedSettings.Closed += onClosed;
        }
        settings.OpenCentered(AppWindow.Id);
    }

    private void OnLanguageChanged()
    {
        Post(() =>
        {
            if (closing) return;
            view.Language = WinUiLanguage.English ? "en-US" : "zh-CN";
            tray?.SetTip(WinUiLanguage.T("AirStereo · 立体声 AirPlay 发送器"));
            RefreshOnUiThread();
        });
    }

    private void ToggleVisibility()
    {
        if (popupVisible) HideWindow(); else Open();
    }

    private void ScheduleDismissIfOutside()
    {
        nint foreground = WinUiNative.GetForegroundWindow();
        if (ShouldDismissPopup(popupVisible, closing, settings != null, tray?.MenuOpen == true,
            hwnd, foreground, foreground == 0 ? 0 : WinUiNative.GetAncestor(foreground, 3)))
        {
            if (!dismiss.IsRunning) dismiss.Start();
        }
        else dismiss.Stop();
    }

    internal static bool ShouldDismissPopup(bool visible, bool exiting, bool settingsOpen, bool menuOpen,
        nint popup, nint foreground, nint foregroundOwner) =>
        visible && !exiting && !settingsOpen && !menuOpen && foreground != 0 &&
        foreground != popup && foregroundOwner != popup;

    internal void Open()
    {
        if (closing) return;
        if (popupVisible)
        {
            dismiss.Stop();
            Activate();
            WinUiNative.SetForegroundWindow(hwnd);
            return;
        }
        visibilityVersion++;
        popupVisible = true;
        dismiss.Stop();
        // Apply the persisted theme before the HWND becomes visible. In
        // particular, this avoids one light frame while WinUI resources and
        // DWM catch up to an explicitly selected dark appearance.
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
        RefreshOnUiThread();
        PositionPopup();
        motion.Reset(view.Root);
        // Only the inner content animates; the outer themed background stays
        // opaque from the first frame, including repeated tray openings.
        AppWindow.Show();
        Activate();
        WinUiNative.SetForegroundWindow(hwnd);
        // Composition visuals must be accessed only after XAML has attached
        // them to the window compositor. Loaded handles the first opening.
        if (view.Root.IsLoaded)
        {
            motion.Enter(view.Root);
            UpdateScanVisuals();
        }
        if (!startupScanStarted)
        {
            BeginStartupScan();
        }
        else if (controller.Groups.Count == 0 && !controller.Scanning)
            controller.StartScan();
    }

    internal static bool ShouldStartHidden(bool startupLaunch, bool silentStartup, bool trayRegistered) =>
        startupLaunch && silentStartup && trayRegistered;

    internal void InitializeLaunch(bool startupLaunch)
    {
        if (ShouldStartHidden(startupLaunch, controller.SilentStartup, tray?.Registered == true))
        {
            // Never activate/show first and then hide: silent login must not
            // flash a window or steal focus. Discovery/auto-connect still run.
            AppWindow.Hide();
            BeginStartupScan();
        }
        else Open();
    }

    private void BeginStartupScan()
    {
        if (startupScanStarted) return;
        startupScanStarted = true;
        controller.StartScan(connectPreviousSelection: true);
    }

    private void PositionPopup()
    {
        if (tray != null)
        {
            try
            {
                (WinUiNative.Rect work, double scale) = tray.WorkArea();
                int margin = (int)Math.Round(12 * scale);
                int width = Math.Min((int)Math.Round(400 * scale), work.Right - work.Left - 2 * margin);
                // The popup contains the device list, playback controls, and volume
                // controls.  A 512px popup leaves the star-sized device area with
                // almost no space, so keep a usable Fluent tray layout while still
                // respecting the available work area on shorter displays.
                int height = Math.Min((int)Math.Round(512 * scale), work.Bottom - work.Top - 2 * margin);
                int x = Math.Max(work.Left, work.Right - width - margin);
                int y = Math.Max(work.Top, work.Bottom - height - margin);
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            }
            catch { }
        }
    }

    private void UpdateScanVisuals()
    {
        view.RefreshButtonControl.IsEnabled = !controller.Scanning && !controller.Playing;
        if (!view.Root.IsLoaded) return;
        motion.Spin(view.RefreshIconControl, controller.Scanning && popupVisible && !closing);
    }

    private async void HideWindow(bool immediate = false)
    {
        dismiss.Stop();
        if (!popupVisible && !immediate) return;
        popupVisible = false;
        int version = ++visibilityVersion;
        motion.Spin(view.RefreshIconControl, false);
        int delay = immediate ? 0 : motion.Exit(view.Root);
        if (delay > 0) await Task.Delay(delay);
        // A rapid tray click may reopen the popup while its fade-out completes.
        if (closing || version != visibilityVersion) return;
        AppWindow.Hide();
        motion.Reset(view.Root);
    }

    private void ExitApplication()
    {
        if (closing) return;
        closing = true;
        visibilityVersion++;
        dismiss.Stop();
        localMuteTimer.Stop();
        motion.Dispose();
        WinUiTheme.Changed -= OnThemeChanged;
        WinUiLanguage.Changed -= OnLanguageChanged;
        controller.Changed -= Refresh;
        foreach (var row in deviceRows.Values) row.Dispose();
        deviceRows.Clear();
        // The settings window is a separate WinUI Window. Close it before
        // disposing the shared controller so the application never leaves a
        // stale settings window or a callback pointing at disposed state.
        WinUiSettingsWindow? openSettings = settings;
        settings = null;
        try { openSettings?.Close(); } catch { }
        controller.Dispose();
        tray?.Dispose();
        Close();
    }

    private void Post(Action action) => dispatcher.TryEnqueue(() => action());

    private void OnThemeChanged(WinUiAppearanceMode _) => dispatcher.TryEnqueue(() =>
    {
        if (closing) return;
        WinUiTheme.Apply(view);
        ApplyWindowTheme();
        Refresh();
    });

    private void ApplyWindowTheme() => WinUiNative.ApplyWindowTheme(hwnd, WinUiTheme.IsDark(view));

    private static TextBlock Text(string value, double _, bool strong) => new()
    {
        Text = value,
        Style = Resource<Style>(strong ? "AirStereoPrimaryTextStyle" : "AirStereoSecondaryTextStyle")
    };

    private static T? Resource<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out object value) ? value as T : null;
}
