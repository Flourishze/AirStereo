using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AirStereo2;

public sealed partial class ShellView : UserControl
{
    readonly BridgeClient client;
    readonly Dictionary<string, DeviceRow> rows = new();
    readonly TextBlock emptyDevices;
    bool updating, applying, masterEditing;
    readonly DispatcherTimer volumeDelay = new() { Interval = TimeSpan.FromMilliseconds(100) };
    internal event Action? SettingsRequested;
    internal event Action? HideRequested;
    internal event Action? VolumeApplied;
    internal event Action? ScanStateChanged;
    internal Grid Root => LayoutRoot;
    internal FontIcon RefreshIconControl => RefreshIcon;
    internal Grid ApplyVolumeIconControl => ApplyVolumeIcon;
    internal bool IsScanning => client.Ready && client.Snapshot.Flag("scanning");
    bool hadConnectedSession;
    internal bool HasQuickConnectTarget => client.Ready && (
        client.Snapshot.List("devices").Any(d => d.Flag("IsDefaultOutput")) ||
        rows.Values.Any(r => r.Selected));

    internal async Task QuickConnectAsync()
    {
        if (applying || !client.Ready) return;
        applying = true; Play.IsEnabled = false;
        try {
            var devices = client.Snapshot.List("devices");
            if (devices.Any(d => d.Flag("IsConnected") || d.Flag("IsConnecting"))) return;
            var targets = devices.Where(d => d.Flag("IsDefaultOutput")).Select(d => d.Text("id")).ToArray();
            if (targets.Length == 0)
                targets = rows.Values.Where(r => r.Selected).Select(r => r.Id).ToArray();
            foreach (string id in targets)
                await client.Command("ToggleConnectionCommand", id);
        } finally { applying = false; Refresh(); }
    }
    int presentation;
    internal void Present(int mode) {
        presentation = mode;
        DevicePanel.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        PlaybackCard.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        VolumeCard.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        LayoutRoot.RowDefinitions[1].Height = mode == 1 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        foreach (var row in rows.Values) row.Dock = mode == 2;
    }

    internal ShellView(BridgeClient bridge)
    {
        InitializeComponent();
        client = bridge;
        emptyDevices = new TextBlock {
            Style = Style("AirStereo2SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 4, 4, 4)
        };
        Volume.Value = 50;
        RealtimeVolume.IsChecked = UiPreferences.Read("realtimeVolume", true);
        RealtimeVolume.Click += (_, _) => { UiPreferences.Set("realtimeVolume", RealtimeVolume.IsChecked == true); if (RealtimeVolume.IsChecked == true) _ = client.Set("MasterVolume", Math.Round(Volume.Value), "main"); };
        Volume.ValueChanged += (_, _) => {
            VolumeValue.Text = Math.Round(Volume.Value) + "%";
            if (updating) return;
            masterEditing = true;
            volumeDelay.Stop(); volumeDelay.Start();
        };
        volumeDelay.Tick += async (_, _) => {
            volumeDelay.Stop();
            if (RealtimeVolume.IsChecked == true) await client.Set("MasterVolume", Math.Round(Volume.Value), "main");
            masterEditing = false;
        };
        ApplyVolume.Click += async (_, _) => { VolumeApplied?.Invoke(); masterEditing = true; await client.Set("MasterVolume", Math.Round(Volume.Value), "main"); masterEditing = false; };
        RefreshButton.Click += async (_, _) => await client.Command("RefreshCommand", "home");
        Settings.Click += (_, _) => SettingsRequested?.Invoke();
        Hide.Click += (_, _) => HideRequested?.Invoke();
        Play.Click += async (_, _) => {
            if (applying || !client.Ready) return;
            applying = true; Play.IsEnabled = false;
            try {
                var devices = client.Snapshot.List("devices");
                var preempted = devices.Where(d => d.Flag("IsPreempted")).ToArray();
                if (devices.Any(d => d.Flag("IsConnected") || d.Flag("IsConnecting"))) {
                    await client.Command("DisconnectAllCommand", "main");
                } else if (preempted.Length > 0) {
                    foreach (var d in preempted)
                        await client.Command("ToggleConnectionCommand", d.Text("id"));
                } else {
                    foreach (var row in rows.Values.Where(r => r.Selected).ToArray())
                        await client.Command("ToggleConnectionCommand", row.Id);
                }
            } finally { applying = false; Refresh(); }
        };
        StopWaiting.Click += async (_, _) => {
            if (applying || !client.Ready) return;
            applying = true; StopWaiting.IsEnabled = false;
            try {
                foreach (var d in client.Snapshot.List("devices").Where(d => d.Flag("IsPreempted")))
                    await client.Command("StopWaitingForReclaimCommand", d.Text("id"));
            } finally { applying = false; StopWaiting.IsEnabled = true; Refresh(); }
        };
        client.Changed += Refresh;
        client.Error += message => { Status.Text = message; UiPreferences.Record(message); };
        Status.Text = WinUiLanguage.T("正在启动设备服务…");
        emptyDevices.Text = WinUiLanguage.T("正在扫描局域网中的 AirPlay 音响…");
        Devices.Children.Add(emptyDevices);
        VolumeValue.Text = "50%";
        WinUiLanguage.Changed += Refresh;
        InAppUpdater.StateChanged += RefreshUpdateBadge;
        RefreshUpdateBadge();
    }

    internal void RefreshUpdateBadge()
    {
        DispatcherQueue.TryEnqueue(() => {
            UpdateDot.Visibility = InAppUpdater.HasPendingUpdate ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    internal void Refresh()
    {
        if (!client.Ready) {
            Status.Text = WinUiLanguage.T("正在启动设备服务…");
            emptyDevices.Text = WinUiLanguage.T("正在扫描局域网中的 AirPlay 音响…");
            return;
        }
        updating = true;
        try {
            JsonElement snapshot = client.Snapshot;
            bool scanning = snapshot.Flag("scanning");
            var devices = snapshot.List("devices");
            var settings = snapshot.Field("settings");
            bool autoReconnect = settings.Flag("AutomaticReceiverRecoveryEnabled");
            if (devices.Length == 0) {
                foreach (string id in rows.Keys.ToArray()) { Devices.Children.Remove(rows[id].Root); rows.Remove(id); }
                emptyDevices.Text = WinUiLanguage.T(scanning ? "正在扫描局域网中的 AirPlay 音响…" : "未发现音响，请确认电脑和音响在同一局域网。");
                if (Devices.Children.Count != 1 || !ReferenceEquals(Devices.Children[0], emptyDevices)) {
                    Devices.Children.Clear();
                    Devices.Children.Add(emptyDevices);
                }
            } else {
                Devices.Children.Remove(emptyDevices);
                var ids = devices.Select(d => d.Text("id")).ToHashSet();
                foreach (string id in rows.Keys.Where(id => !ids.Contains(id)).ToArray()) { Devices.Children.Remove(rows[id].Root); rows.Remove(id); }
                foreach (var device in devices) {
                    string id = device.Text("id");
                    if (!rows.TryGetValue(id, out var row)) { row = new DeviceRow(client, device); rows[id] = row; Devices.Children.Add(row.Root); row.SelectionChanged += Refresh; }
                    row.AutoReconnect = autoReconnect;
                    row.Update(device);
                    row.Dock = presentation == 2;
                }
            }
            bool connected = devices.Any(d => d.Flag("IsConnected"));
            bool connecting = devices.Any(d => d.Flag("IsConnecting"));
            bool standby = devices.Any(d => d.Flag("IsStandby"));
            var preempted = devices.Where(d => d.Flag("IsPreempted")).ToArray();
            var failed = devices.Where(d => d.Flag("IsFailed")).ToArray();
            if (connected) hadConnectedSession = true;
            else if (!connecting && preempted.Length == 0 && failed.Length == 0) hadConnectedSession = false;

            RefreshButton.IsEnabled = !scanning;
            if (!masterEditing && RealtimeVolume.IsChecked == true) Volume.Value = snapshot.Number("masterVolume", 50);
            VolumeValue.Text = Math.Round(Volume.Value) + "%";
            string statusBase;
            if (preempted.Length > 0 && !connected) {
                string names = string.Join("、", preempted.Select(d => "「" + d.Text("Name", WinUiLanguage.T("未知音响")) + "」"));
                statusBase = autoReconnect
                    ? string.Format(WinUiLanguage.T("已被接管，正在重连 · {0}"), names)
                    : string.Format(WinUiLanguage.T("已被其他设备接管 · {0}"), names);
            } else if (failed.Length > 0 && !connected && !connecting) {
                var f = failed[0];
                string fname = f.Text("Name", WinUiLanguage.T("未知音响"));
                string fip = f.Text("IPAddress");
                string reason = f.Text("FailureMessage").Trim();
                string targetLabel = string.IsNullOrEmpty(fip) ? $"「{fname}」" : $"「{fname}」({fip})";
                statusBase = string.IsNullOrEmpty(reason)
                    ? string.Format(WinUiLanguage.T("连接失败 · 音响 {0}"), targetLabel)
                    : string.Format(WinUiLanguage.T("连接失败 · 音响 {0}：{1}"), targetLabel, reason);
            } else if (scanning && !connected && !connecting) {
                statusBase = WinUiLanguage.T("扫描中…");
            } else if (connecting) {
                statusBase = WinUiLanguage.T(hadConnectedSession ? "连接中断，正在重连…" : "连接中…");
            } else if (standby && !connected) {
                statusBase = WinUiLanguage.T("静音待机中");
            } else if (connected) {
                statusBase = WinUiLanguage.T("播放中");
            } else if (devices.Length == 0) {
                statusBase = WinUiLanguage.T("未发现音响");
            } else {
                statusBase = WinUiLanguage.T(rows.Values.Any(r => r.Selected) ? "已选择" : "请选择音响");
            }
            string latencyDisplay = ResolveLatencyDisplay(devices, settings, connected || connecting);
            Status.Text = string.IsNullOrEmpty(latencyDisplay) ? statusBase : statusBase + " · " + latencyDisplay;
            string configuredCodec = settings.Text("SelectedAudioCodec", "ALAC").Trim();
            if (string.IsNullOrEmpty(configuredCodec)) configuredCodec = "ALAC";
            string activeCodec = (connected || connecting) ? snapshot.Text("activeCodec").Trim() : "";
            string codec = string.IsNullOrEmpty(activeCodec) ? configuredCodec : activeCodec;
            int configuredSr = (int)snapshot.Number("sampleRateHz", UiPreferences.Read("sampleRateHz", 44100));
            int activeSr = (connected || connecting) ? (int)snapshot.Number("activeSampleRateHz", 0) : 0;
            int displaySr = activeSr > 0 ? activeSr : configuredSr;
            string srLabel = displaySr == 48000 ? "48 kHz" : "44.1 kHz";
            string activeProto = (connected || connecting) ? snapshot.Text("activeProtocol").Trim() : "";
            string protoText = (connected || connecting) && !string.IsNullOrEmpty(activeProto)
                ? activeProto
                : ((int)snapshot.Number("protocolMode", UiPreferences.Read("protocolMode", 0)) switch {
                    1 => "AirPlay 1",
                    2 => "AirPlay 2",
                    _ => ""
                });
            if (!string.IsNullOrEmpty(protoText)) {
                ProtocolLabel.Text = protoText;
                string effSr = protoText.Equals("AirPlay 1", StringComparison.OrdinalIgnoreCase) ? "44.1 kHz" : srLabel;
                Format.Text = codec + " · " + effSr;
            } else {
                ProtocolLabel.Text = codec;
                Format.Text = srLabel;
            }
            if (connected || connecting) {
                PlayText.Text = WinUiLanguage.T("停止播放");
                PlayIcon.Glyph = "\uE71A";
            } else if (preempted.Length > 0) {
                PlayText.Text = WinUiLanguage.T("立即抢回连接");
                PlayIcon.Glyph = "\uE72C";
            } else {
                PlayText.Text = WinUiLanguage.T("播放电脑声音");
                PlayIcon.Glyph = "\uE768";
            }
            StopWaiting.Content = WinUiLanguage.T(autoReconnect ? "停止自动重连" : "清除接管提示");
            StopWaiting.Visibility = (!connected && preempted.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
            Play.IsEnabled = !applying && (connected || connecting || preempted.Length > 0 || rows.Values.Any(r => r.Selected));
            ScanStateChanged?.Invoke();
        } finally { updating = false; }
    }

    static string ResolveLatencyDisplay(JsonElement[] devices, JsonElement settings, bool active)
    {
        if (active) {
            var measured = devices
                .Where(d => d.Flag("IsConnected") || d.Flag("IsConnecting"))
                .Select(d => FormatLatencyText(d.Text("LatencyText")))
                .Where(text => text.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (measured.Length > 0) return string.Join(" / ", measured);
        }
        int mode = (int)settings.Number("StreamingModeIndex");
        double requested = settings.Number("CurrentLatencyMs");
        if (mode == 0) return WinUiLanguage.T("自动");
        if (mode == 1) return "200 ms";
        if (mode == 2) return "500 ms";
        if (mode == 3) {
            double custom = settings.Number("CustomBufferSize", requested > 0 ? requested : 200);
            return Math.Round(custom) + " ms";
        }
        return WinUiLanguage.T("自动");
    }

    static string FormatLatencyText(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0 || text.Contains("--", StringComparison.Ordinal)) return "";
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) && !text.EndsWith(" ms", StringComparison.OrdinalIgnoreCase))
            return text[..^2].TrimEnd() + " ms";
        return text;
    }

    internal new static Style Style(string key) => (Style)Application.Current.Resources[key];
    internal static TextBlock Text(string value, bool secondary = false) {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = Style(secondary ? "AirStereo2SecondaryTextStyle" : "AirStereo2PrimaryTextStyle") };
        WinUiLanguage.SetText(text, value); return text;
    }

    sealed class DeviceRow
    {
        readonly BridgeClient client;
        readonly CheckBox select = new() { Width = 22, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock name = Text(""), detail = Text("", true), online = Text("在线", true);
        readonly TextBlock role = new() { Style = Style("AirStereo2RoleTextStyle"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly FontIcon icon = new() { Glyph = "\uE7F4", Style = Style("AirStereo2AccentIconStyle"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        JsonElement state;
        internal string Id { get; }
        internal bool Selected => select.IsChecked == true;
        internal Border Root { get; }
        internal event Action? SelectionChanged;
        internal bool Dock { get; set; }
        internal bool AutoReconnect { get; set; }
        internal DeviceRow(BridgeClient bridge, JsonElement value)
        {
            client = bridge; state = value; Id = value.Text("id");
            string key = Key(value);
            select.IsChecked = UiPreferences.Read("selected:" + key, true);
            AutomationProperties.SetName(select, "选择 " + value.Text("Name"));
            var grid = new Grid { Padding = new Thickness(7, 5, 7, 5), ColumnSpacing = 4, MinHeight = 56 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var speaker = new Border { Style = Style("AirStereo2SpeakerIconStyle"), Child = icon, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.FontSize = 14;
            online.Style = Style("AirStereo2OnlineTextStyle"); online.FontSize = 12; online.VerticalAlignment = VerticalAlignment.Center;
            detail.FontSize = 12; detail.VerticalAlignment = VerticalAlignment.Center; detail.TextWrapping = TextWrapping.Wrap;
            var detailRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            detailRow.Children.Add(online);
            var separator = Text("·", true); separator.FontSize = 12; separator.VerticalAlignment = VerticalAlignment.Center; detailRow.Children.Add(separator);
            detailRow.Children.Add(detail);
            labels.Children.Add(name); labels.Children.Add(detailRow);
            grid.Children.Add(select); grid.Children.Add(speaker); Grid.SetColumn(speaker, 1);
            grid.Children.Add(labels); Grid.SetColumn(labels, 2);
            grid.Children.Add(role); Grid.SetColumn(role, 3);
            Root = new Border { Child = grid, Style = Style(Selected ? "AirStereo2SelectedDeviceCardStyle" : "AirStereo2DeviceCardStyle"), Margin = new Thickness(0, 0, 0, 6) };
            select.Click += (_, _) => {
                UiPreferences.Set("selected:" + key, Selected);
                Root.Style = Style(Selected ? "AirStereo2SelectedDeviceCardStyle" : "AirStereo2DeviceCardStyle");
                SelectionChanged?.Invoke();
            };
        }
        static string Key(JsonElement d) => d.Text("key", d.Text("Name") + "|" + d.Text("IPAddress"));
        internal void Update(JsonElement value)
        {
            state = value;
            name.Text = value.Text("Name");
            int members = value.List("members").Length;
            string kind = value.Flag("IsNativeStereoPair") ? string.Format(WinUiLanguage.T("原生立体声对 · {0} 只"), members) : members > 1 ? string.Format(WinUiLanguage.T("{0} 只 · 组合目标"), members) : WinUiLanguage.T("独立音响");
            string status = value.Flag("IsPreempted")
                ? (AutoReconnect ? "正在重连" : "已被接管")
                : value.Flag("IsFailed")
                    ? "连接失败"
                    : value.Flag("IsConnecting")
                        ? "连接中"
                        : value.Flag("IsStandby")
                            ? "静音待机"
                            : value.Flag("IsConnected")
                                ? "播放中"
                                : "在线";
            online.Text = WinUiLanguage.T(status);
            detail.Text = kind;
            role.Text = !Selected ? "" : value.Flag("IsNativeStereoPair") ? WinUiLanguage.T("配对") : "✓";
            Root.Style = Style(Selected ? "AirStereo2SelectedDeviceCardStyle" : "AirStereo2DeviceCardStyle");
            select.IsEnabled = !client.Snapshot.Flag("scanning");
            ToolTipService.SetToolTip(Root, value.Flag("IsFailed") ? value.Text("FailureMessage") : value.Text("IPAddress"));
        }
    }
}
