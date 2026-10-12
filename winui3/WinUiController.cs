using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AirStereo.Audio;
using AirStereo.Protocol;
using AirStereo.Session;
using AirStereo.Ui;

namespace AirStereo;

/// <summary>
/// Presentation-neutral 1.0.6 controller.  It intentionally calls the same public
/// AirStereoApi/PlaybackRoute chain as the released WinForms surface; WinUI only binds
/// controls to this state and never implements a second AirPlay protocol path.
/// </summary>
internal sealed class WinUiController : IDisposable
{
    private readonly Action<Action> post;
    private readonly object stateGate = new();
    private readonly HashSet<string> selectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> selectionOrder = new();
    // Persist explicit user choices separately from the currently visible rows.
    // A temporary discovery miss must not erase the next startup's targets.
    private string savedSelection = "";
    private readonly AudioProfileController calibration = new();
    private readonly ObservableCollection<ReceiverGroup> groups = new();
    private readonly int silenceDisconnectMilliseconds;
    private readonly LocalOutputMuteController? localMute;
    private ManualResetEventSlim? stopSignal;
    private LivePlaybackControl? livePlayback;
    private bool disposed;
    private bool volumeBusy;
    private bool muteAbandoned;
    private int balancePreference;
    private bool calibrationEnabled;
    private string calibrationBands = "0,0,0,0,0,0,0,0,0,0";
    private string equalizerPresetName = "平坦";
    private bool equalizerPresetSeen;
    private string? currentCodec;
    private Thread? playbackThread;
    private bool autoConnectAfterScan;
    private readonly bool isolatedTest;
    private readonly string? isolatedSettingsDirectory;
    private readonly FaultStore diagnosticsStore;
    private long activityRevision;
    private long faultRevision;

    internal event Action? Changed;
    internal event Action<string>? Notice;
    internal event Action? FaultsChanged;
    internal long ActivityRevision => Interlocked.Read(ref activityRevision);
    internal long FaultRevision => Interlocked.Read(ref faultRevision);

    internal ReadOnlyObservableCollection<ReceiverGroup> Groups { get; }
    internal bool Scanning { get; private set; }
    internal bool Playing { get; private set; }
    internal bool StreamReady { get; private set; }
    internal bool RecoveringConnection { get; private set; }
    internal bool NetworkUnavailable { get; private set; }
    internal bool VolumeBusy => volumeBusy;
    internal string Status { get; private set; } = "正在准备…";
    internal string CurrentCodec => currentCodec ?? "";
    internal string LastNotice { get; private set; } = "";
    internal bool UseAlac { get; set; }
    internal int SampleRate { get; set; } = 44100;
    internal SilenceFrameMode SilenceMode { get; set; }
    internal LatencyMode LatencyMode { get; private set; } = LatencyMode.Normal;
    internal int CustomLatencyMs { get; private set; } = LatencyProfile.NormalMs;
    internal int CurrentLatencyMs => LatencyProfile.Resolve(LatencyMode, CustomLatencyMs);
    internal int Volume { get; private set; } = 65;
    internal bool RealtimeVolume { get; private set; }
    internal int Balance => balancePreference;
    internal bool PlaybackAutoReconnect { get; private set; } = true;
    internal int MaxPlaybackReconnectAttempts { get; private set; } = 3;
    internal bool MuteLocalOutput { get; private set; }
    internal bool AutoConnect { get; private set; }
    internal bool StartupEnabled { get; private set; }
    internal bool SilentStartup { get; private set; }
    internal AudioProfile CalibrationProfile => calibration.Current;
    internal string EqualizerPresetName => equalizerPresetName;
    internal string DiagnosticsDirectory => diagnosticsStore.DirectoryPath;
    internal IReadOnlyList<string> RecentActivity => diagnosticsStore.ActivitySnapshot();
    internal bool HasBalance => SelectedRoute()?.SupportsBalance == true;
    internal bool HasSelection => SelectedGroups().Count > 0;
    internal bool CanTestLeft => HasSelection && !Scanning && (!Playing || StreamReady);
    internal bool CanTestRight => CanTestLeft && SelectedRoute()?.SplitStereo == true;
    internal bool CanPlay => SelectedRoute() != null && !Scanning && !Playing;
    internal bool CanStop => Playing;
    internal bool CanApplyVolume => HasSelection && !volumeBusy && !RealtimeVolume;

    internal WinUiController(Action<Action> post, int silenceDisconnectMilliseconds, bool useAlac,
        SilenceFrameMode silenceMode, bool isolatedTest = false, FaultStore? diagnosticsStore = null,
        string? isolatedSettingsDirectory = null)
    {
        if (isolatedSettingsDirectory != null && !isolatedTest) throw new ArgumentException("Test settings require an isolated controller.");
        this.isolatedTest = isolatedTest;
        this.isolatedSettingsDirectory = isolatedSettingsDirectory;
        this.diagnosticsStore = diagnosticsStore ?? FaultStore.Default;
        this.post = post;
        this.silenceDisconnectMilliseconds = silenceDisconnectMilliseconds > 0
            ? silenceDisconnectMilliseconds : SessionOptions.DefaultSourceSilenceDisconnectMilliseconds;
        UseAlac = useAlac;
        SilenceMode = silenceMode;
        Groups = new ReadOnlyObservableCollection<ReceiverGroup>(groups);
        // Offline tests never restore mute, touch user preferences, or alter
        // startup registration. Persistence tests may use their own directory.
        if (isolatedTest)
        {
            if (isolatedSettingsDirectory != null) LoadSettings();
            return;
        }
        try
        {
            localMute = new LocalOutputMuteController(new WindowsOutputMuteBackend(),
                new FileMuteRecoveryStore(Path.Combine(PackageEnvironment.DataDirectory, "local-output-mute-recovery.txt")), Log);
            localMute.Restore();
        }
        catch (Exception error) { Log("本机静音初始化失败（忽略）：" + error.Message); }
        LoadSettings();
    }

    internal void StartScan(bool connectPreviousSelection = false)
    {
        if (disposed || Scanning || Playing) return;
        autoConnectAfterScan = connectPreviousSelection;
        Scanning = true;
        NetworkUnavailable = false;
        SetStatus("扫描中…");
        Log("开始扫描（约 6 秒）");
        RaiseChanged();
        Task.Run(() =>
        {
            try
            {
                Discovery found = AirStereoApi.Discover(6);
                post(() => ApplyDiscovery(found));
            }
            catch (Exception error)
            {
                post(() =>
                {
                    if (disposed) return;
                    Scanning = false;
                    autoConnectAfterScan = false;
                    Log("扫描失败：" + error.Message);
                    RecordFault("扫描失败", error);
                    SetStatus("连接失败 · 扫描失败");
                    RaiseChanged();
                });
            }
        });
    }

    private void ApplyDiscovery(Discovery found)
    {
        if (disposed) return;
        Scanning = false;
        bool connectPreviousSelection = autoConnectAfterScan;
        autoConnectAfterScan = false;
        NetworkUnavailable = found.Last != null && found.Last.InterfacesJoined == 0;
        groups.Clear();
        foreach (ReceiverGroup group in found.Groups) groups.Add(group);
        bool previousSelectionComplete = ResolveStartupRoute(groups, selectionOrder) != null;
        RestoreSelectionAfterScan();
        if (found.Last != null)
            foreach (string warning in found.Last.Warnings) Log("警告：" + warning);

        if (NetworkUnavailable)
        {
            Log("这台电脑没有找到可用的 IPv4 网络接口，无法搜索。");
            RecordFault("网络不可用", "未找到可用的 IPv4 网络接口");
            SetStatus("网络不可用");
            RaiseChanged();
            return;
        }
        if (groups.Count == 0)
        {
            Log("没有发现 AirPlay 音箱。确认电脑和音箱在同一个局域网，然后刷新重试。");
            SetStatus("未发现音响");
            RaiseChanged();
            return;
        }
        StringBuilder summary = new();
        summary.Append("发现 ").Append(groups.Count).Append(" 个目标，")
            .Append(found.Receivers.Count).Append(" 个音箱端点");
        if (found.Rounds > 1) summary.Append("（重新扫描了 ").Append(found.Rounds).Append(" 轮）");
        Log(summary.ToString());
        foreach (ReceiverGroup group in groups) Log("  " + TargetTitle(group) + "  " + TargetDetail(group));
        // A completed discovery must not leave the tray control showing the
        // transient scan state.  Selection restoration happens before this
        // status is chosen, so the play button and status agree immediately.
        SetStatus(HasSelection ? "已选择" : "请选择音响");
        RaiseChanged();
        if (connectPreviousSelection && AutoConnect && previousSelectionComplete)
        {
            Log("正在自动连接上次选择的音响…");
            StartPlayback("loopback");
        }
        else if (connectPreviousSelection && AutoConnect)
            Log("上次选择的音响未全部找到或配对信息不完整，未自动连接；请刷新或重新选择。");
    }

    internal static PlaybackRoute? ResolveStartupRoute(IList<ReceiverGroup> available, IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0 || wanted.Count > 2) return null;
        List<ReceiverGroup> selected = new();
        foreach (string key in wanted)
        {
            List<ReceiverGroup> matches = available.Where(group =>
                string.Equals(SelectionKey(group), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || selected.Contains(matches[0])) return null;
            // Name/IP fallback identities are unsuitable for unattended playback.
            if (matches[0].Members.Any(member => string.IsNullOrWhiteSpace(member.DeviceId) &&
                string.IsNullOrWhiteSpace(member.Host))) return null;
            selected.Add(matches[0]);
        }
        return PlaybackRoute.Resolve(selected);
    }

    private void RestoreSelectionAfterScan()
    {
        HashSet<string> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReceiverGroup group in groups) available.Add(SelectionKey(group));
        selectedKeys.RemoveWhere(key => !available.Contains(key));
        selectionOrder.RemoveAll(key => !selectedKeys.Contains(key));
        ReceiverGroup? exclusive = SelectedGroups().FirstOrDefault(group => !PlaybackRoute.Independent(group));
        if (exclusive == null) return;
        string key = SelectionKey(exclusive);
        selectedKeys.Clear(); selectionOrder.Clear(); selectedKeys.Add(key); selectionOrder.Add(key);
    }

    internal bool IsSelected(ReceiverGroup group) => selectedKeys.Contains(SelectionKey(group));

    internal string Select(ReceiverGroup group, bool selected)
    {
        if (Scanning || Playing) return "播放期间不能调整设备选择，请先停止。";
        string key = SelectionKey(group);
        if (!selected)
        {
            selectedKeys.Remove(key); selectionOrder.RemoveAll(value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
            UpdateRouteState();
            return "";
        }
        if (!PlaybackRoute.Independent(group) || group.IsStereoPair)
        {
            selectedKeys.Clear(); selectionOrder.Clear(); selectedKeys.Add(key); selectionOrder.Add(key);
        }
        else
        {
            List<ReceiverGroup> current = SelectedGroups().ToList();
            if (current.Count >= 2)
            {
                LastNotice = "最多选择两只独立音响。";
                Notice?.Invoke(LastNotice);
                return LastNotice;
            }
            selectedKeys.Add(key); selectionOrder.Add(key);
        }
        UpdateRouteState();
        return "";
    }

    internal IReadOnlyList<ReceiverGroup> SelectedGroups() => ResolveSelectedGroups();

    private List<ReceiverGroup> ResolveSelectedGroups()
    {
        List<ReceiverGroup> result = new();
        foreach (string key in selectionOrder)
        {
            if (!selectedKeys.Contains(key)) continue;
            ReceiverGroup? group = groups.FirstOrDefault(item => string.Equals(SelectionKey(item), key, StringComparison.OrdinalIgnoreCase));
            if (group != null) result.Add(group);
        }
        return result;
    }

    internal PlaybackRoute? SelectedRoute() => PlaybackRoute.Resolve(ResolveSelectedGroups());

    internal string Role(ReceiverGroup group)
    {
        if (!IsSelected(group)) return "";
        if (group.IsStereoPair) return "配对";
        List<ReceiverGroup> selected = ResolveSelectedGroups();
        return selected.Count == 2 ? (string.Equals(SelectionKey(selected[0]), SelectionKey(group), StringComparison.OrdinalIgnoreCase) ? "L" : "R") : "✓";
    }

    internal string RouteSummary()
    {
        List<ReceiverGroup> selected = ResolveSelectedGroups();
        PlaybackRoute? route = SelectedRoute();
        if (selected.Count == 0) return "未选择音响";
        if (selected.Count == 2) return "L · " + selected[0].Name + "　R · " + selected[1].Name + " · 双设备立体声";
        if (route?.NativePair == true) return "原生立体声对 · 接收端分配 L/R · 完整立体声";
        return "单设备 · 完整立体声";
    }

    internal string TargetTitle(ReceiverGroup group)
    {
        string name = !string.IsNullOrWhiteSpace(group.Name) ? group.Name : group.Members.FirstOrDefault()?.Instance ?? "未知音响";
        if (group.IsStereoPair) return name + " · 立体声对 · 2 只";
        if (PlaybackRoute.Independent(group)) return name + " · 独立音响";
        if (group.StereoPairId.Length > 0) return name + " · 配对音响";
        if (group.IsSuspectedPair) return name + " · 独立音响";
        if (group.IsGroup) return name + " · 组合（" + group.Members.Count + " 只）";
        return name + " · 独立音响";
    }

    internal string TargetDetail(ReceiverGroup group)
    {
        StringBuilder text = new();
        foreach (Receiver member in group.Members)
        {
            if (text.Length > 0) text.Append(" · ");
            text.Append(string.IsNullOrWhiteSpace(member.Address) ? "离线" : "在线 · " + member.Address);
            if (!string.IsNullOrWhiteSpace(member.Model)) text.Append(" · ").Append(member.Model);
        }
        return text.ToString();
    }

    internal string TargetState(ReceiverGroup group)
    {
        if (group.IsStereoPair) return "在线 · 原生配对";
        if (IsSelected(group) && Playing) return StreamReady ? "播放中" : RecoveringConnection ? "恢复连接" : "连接中";
        return "在线 · " + (PlaybackRoute.Independent(group) ? "独立音响" : "组合目标");
    }

    internal void SetBalance(int value)
    {
        balancePreference = Math.Max(-100, Math.Min(100, value));
        if (livePlayback != null) livePlayback.Balance = balancePreference;
        SaveSettings(); RaiseChanged();
    }

    internal void ResetBalance() => SetBalance(0);

    internal void SetFormat(bool alac, int rate)
    {
        if (Playing) return;
        UseAlac = alac; SampleRate = rate == 48000 ? 48000 : 44100; SaveSettings(); RaiseChanged();
    }

    internal void SetLatency(LatencyMode mode, int customMs)
    {
        if (Playing) return;
        LatencyMode = mode; CustomLatencyMs = LatencyProfile.Clamp(customMs); SaveSettings(); RaiseChanged();
    }

    internal void SetVolumeValue(int value)
    {
        Volume = Math.Max(0, Math.Min(100, value));
        SaveSettings(); RaiseChanged();
    }

    internal void SetRealtimeVolume(bool value) { RealtimeVolume = value; SaveSettings(); RaiseChanged(); }

    internal void SetCalibration(bool enabled, double[] bands, string presetName)
    {
        // Channel trim/calibration was removed from the settings surface. Keep
        // the profile's legacy fields at zero so old playback and settings data
        // cannot silently apply a separate left/right gain.
        calibration.Update(new AudioProfile(enabled, bands, 0.0, 0.0));
        equalizerPresetName = string.IsNullOrWhiteSpace(presetName) ? "自定义" : presetName;
        SaveSettings();
        RaiseChanged();
    }

    internal void ApplyVolume()
    {
        ReceiverGroup? group = SelectedRoute()?.Target;
        if (group == null || volumeBusy) return;
        volumeBusy = true; RaiseChanged();
        Task.Run(() =>
        {
            try
            {
                List<string> results = AirStereoApi.SetVolume(group, Volume, Srp.DefaultPin, Log);
                foreach (string line in results) Log("音量 " + line);
                Log("音量设置已发送：「" + TargetTitle(group) + "」" + Volume + "%。");
            }
            catch (Exception error) { Log("设置音量失败：" + error.Message); RecordFault("音量设置失败", error); }
            finally { post(() => { volumeBusy = false; RaiseChanged(); }); }
        });
    }

    internal void StartPlayback(string kind)
    {
        if (disposed || Scanning) return;
        PlaybackRoute? route = SelectedRoute();
        ReceiverGroup? group = route?.Target;
        if (group == null) { LastNotice = "请先勾选一只音响，或勾选两只独立音响组成 L/R 立体声。"; Notice?.Invoke(LastNotice); return; }
        if (Playing)
        {
            if ((kind == "left-check" || kind == "right-check") && StreamReady && livePlayback != null)
            {
                livePlayback.StartTest(route?.SplitStereo == true ? (kind == "left-check" ? ChannelTest.Left : ChannelTest.Right) : ChannelTest.Stereo);
                Log("在当前会话中测试声道，测试期间忽略平衡值，结束后恢复电脑声音。");
            }
            return;
        }
        bool split = route.SplitStereo;
        string actualKind = (kind == "left-check" || kind == "right-check") && !split ? "tone" : kind;
        PlayRequest request = new()
        {
            Kind = actualKind,
            SampleRate = SampleRate,
            Mode = LatencyMode,
            CustomLatencyMs = CustomLatencyMs,
            SourceSilenceDisconnectMilliseconds = silenceDisconnectMilliseconds,
            UseAlac = UseAlac,
            SilenceMode = SilenceMode,
            Calibration = calibration,
            SplitStereo = split,
            SwapChannels = false,
            Balance = route.EffectiveBalance(balancePreference),
            UsePtp = true,
            Gain = 1.0,
            DurationMs = kind is "left-check" or "right-check" ? 3000 : 0
        };
        livePlayback = kind == "loopback" ? new LivePlaybackControl(route.SupportsBalance) : null;
        if (livePlayback != null) livePlayback.Balance = request.Balance;
        request.LiveControl = livePlayback;
        stopSignal = new ManualResetEventSlim(false);
        ManualResetEventSlim stop = stopSignal;
        Playing = true; StreamReady = false; RecoveringConnection = false; currentCodec = null; muteAbandoned = false;
        SetStatus(kind == "loopback" ? "连接中…" : "正在播放测试信号…");
        Log(kind == "loopback" ? "开始播放电脑声音到「" + TargetTitle(group) + "」" : "开始声道测试");
        RaiseChanged();
        playbackThread = new Thread(() => RunPlayback(group, request, stop, kind)) { IsBackground = true, Name = "air-stereo-winui-play" };
        playbackThread.Start();
    }

    private void RunPlayback(ReceiverGroup group, PlayRequest request, ManualResetEventSlim stop, string kind)
    {
        Exception? failure = null; PlayResult? result = null; int attempts = 0; bool stoppedByUser = false;
        bool reconnect = kind == "loopback" && PlaybackAutoReconnect && MaxPlaybackReconnectAttempts > 0;
        try
        {
            while (true)
            {
                try { result = AirStereoApi.Play(group, request, stop, PlaybackLog); break; }
                catch (Exception error)
                {
                    if (stop.IsSet) { stoppedByUser = true; break; }
                    failure = error;
                    if (!reconnect || attempts >= MaxPlaybackReconnectAttempts) break;
                    attempts++;
                    post(() => { RecoveringConnection = true; StreamReady = false; SetStatus("恢复连接…"); RaiseChanged(); });
                    Log("播放中断：" + error.Message + "；将在 2 秒后第 " + attempts + "/" + MaxPlaybackReconnectAttempts + " 次重连");
                    if (stop.Wait(2000)) { stoppedByUser = true; break; }
                    failure = null;
                }
            }
        }
        finally
        {
            post(() =>
            {
                Playing = false; StreamReady = false; RecoveringConnection = false; livePlayback = null; volumeBusy = false;
                UpdateLocalMute();
                if (failure != null && !stoppedByUser) { Log("播放中断：" + failure.Message); RecordFault("播放中断", failure); SetStatus("连接失败 · " + failure.Message); }
                else SetStatus("已停止");
                RaiseChanged();
            });
            stop.Dispose();
            if (ReferenceEquals(stopSignal, stop)) stopSignal = null;
        }
    }

    private void PlaybackLog(string message)
    {
        Log(message);
        if (message.StartsWith("Codec diagnostics:", StringComparison.Ordinal))
        {
            string codec = message.Contains(" actual=ALAC ", StringComparison.Ordinal) ? "ALAC" : message.Contains(" actual=PCM ", StringComparison.Ordinal) ? "PCM" : "";
            if (codec.Length > 0 && message.Contains(" setup=accepted ", StringComparison.Ordinal)) post(() => { currentCodec = codec; RaiseChanged(); });
        }
        if (IsPlaybackWarning(message)) RecordFault("连接或同步警告", message);
        if (message.StartsWith("connected:", StringComparison.OrdinalIgnoreCase)) post(() => { RecoveringConnection = false; SetStatus("缓冲中…"); RaiseChanged(); });
        else if (message.StartsWith("streaming ", StringComparison.OrdinalIgnoreCase)) post(() => { StreamReady = true; UpdateLocalMute(); SetStatus("播放中"); RaiseChanged(); });
    }

    internal void StopPlayback()
    {
        if (stopSignal == null) return;
        Log("正在停止播放并断开音箱…"); stopSignal.Set();
    }

    internal void SetAutoReconnect(bool value, int attempts)
    {
        PlaybackAutoReconnect = value; MaxPlaybackReconnectAttempts = Math.Max(1, Math.Min(10, attempts)); SaveSettings(); RaiseChanged();
    }

    internal void SetMuteLocalOutput(bool value)
    {
        MuteLocalOutput = value;
        // A user can enable this while a stream is already running.  The old
        // WinUI surface only persisted the checkbox and waited for a future
        // playback transition, so the current session never got muted.
        if (value)
        {
            muteAbandoned = false;
            UpdateLocalMute();
        }
        else
        {
            localMute?.Restore();
        }
        SaveSettings();
        RaiseChanged();
    }

    // Called by the WinUI dispatcher timer.  Keep the maintenance on the UI
    // thread, like the WinForms Timer path, because the endpoint wrapper is a
    // COM object and this also serializes it with playback state transitions.
    internal void MaintainLocalMute() => UpdateLocalMute();

    internal void SetAutoConnect(bool value) { AutoConnect = value; SaveSettings(); RaiseChanged(); }

    internal void SetStartup(bool value)
    {
        try { StartupService.SetEnabled(value); StartupEnabled = StartupService.Enabled; }
        catch (Exception error) { LastNotice = error.Message; Notice?.Invoke(LastNotice); try { StartupEnabled = StartupService.Enabled; } catch { } }
        RaiseChanged();
    }

    internal void SetSilentStartup(bool value)
    {
        SilentStartup = value;
        SaveSettings();
        RaiseChanged();
    }

    internal List<FaultEntry> ReadFaults() => diagnosticsStore.Read();

    internal void OpenDiagnosticsDirectory()
    {
        try { Directory.CreateDirectory(FaultStore.Default.EnsureDirectory()); Process.Start(new ProcessStartInfo("explorer.exe", '"' + FaultStore.Default.DirectoryPath + '"') { UseShellExecute = true }); }
        catch (Exception error) { LastNotice = "无法打开故障目录：" + error.Message; Notice?.Invoke(LastNotice); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; StopPlayback(); localMute?.Restore(); SaveSettings();
    }

    private void UpdateRouteState()
    {
        savedSelection = EncodeSelection();
        LastNotice = ""; SaveSettings(); SetStatus(SelectedRoute() == null ? "请选择音响" : "已选择"); RaiseChanged();
    }

    private void SetStatus(string status) { Status = status; }
    private void RaiseChanged() { if (!disposed) Changed?.Invoke(); }
    private void Post(Action action) => post(action);

    private void Log(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
        LastNotice = line;
        try { diagnosticsStore.Activity(line); } catch { }
        Interlocked.Increment(ref activityRevision);
        Notice?.Invoke(line);
    }

    private void RecordFault(string category, string message)
    {
        try
        {
            diagnosticsStore.Record(category, message, "", RouteSummary());
            Interlocked.Increment(ref faultRevision);
            FaultsChanged?.Invoke();
        }
        catch { }
    }
    private void RecordFault(string category, Exception error) => RecordFault(category, error.Message);

    private static bool IsPlaybackWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string value = message.ToLowerInvariant();
        return value.Contains("receiver closed the control connection") || value.Contains("control read failed") ||
            value.Contains("invalid authentication tag") || value.Contains("repeated control feedback failures") ||
            value.Contains("has not replied to ptp") || value.Contains("ptp unavailable") ||
            value.Contains("synchronization cannot be verified");
    }

    private void UpdateLocalMute()
    {
        if (localMute == null) return;
        try
        {
            if (!Playing || !StreamReady || !MuteLocalOutput || muteAbandoned) { localMute.Restore(); return; }
            if (localMute.OwnsEndpoint && !localMute.CheckDefault()) muteAbandoned = true;
            if (!muteAbandoned) localMute.Begin();
        }
        catch (Exception error) { Log("本机静音降级（不影响串流）：" + error.Message); }
    }

    private static string ReceiverSelectionKey(Receiver receiver) => "device:" + receiver.Identity;

    internal static string SelectionKey(ReceiverGroup group)
    {
        List<string> ids = group.Members.Select(ReceiverSelectionKey).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        return group.Members.Count > 1 ? "group:" + string.Join(",", ids) : (ids.Count == 1 ? ids[0] : "group:" + group.Name);
    }

    private void LoadSettings()
    {
        try
        {
            string path = SettingsPath();
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                string key = line.Substring(0, eq); string value = line.Substring(eq + 1);
                if (key == "realtimeVolume") RealtimeVolume = value == "1";
                else if (key == "muteLocalOutput") MuteLocalOutput = value == "1";
                else if (key == "autoConnect") AutoConnect = value == "1";
                else if (key == "silentStartup") SilentStartup = value == "1";
                else if (key == "playbackAutoReconnect") PlaybackAutoReconnect = value != "0";
                else if (key == "maxPlaybackReconnectAttempts" && int.TryParse(value, out int attempts)) MaxPlaybackReconnectAttempts = Math.Clamp(attempts, 1, 10);
                else if (key == "volume" && int.TryParse(value, out int volume)) Volume = Math.Clamp(volume, 0, 100);
                else if (key == "stereoBalance" && int.TryParse(value, out int balance)) balancePreference = Math.Clamp(balance, -100, 100);
                else if (key == "latencyMode" && LatencyProfile.TryParse(value, out LatencyMode mode)) LatencyMode = mode;
                else if (key == "latencyCustom" && int.TryParse(value, out int custom)) CustomLatencyMs = LatencyProfile.Clamp(custom);
                else if (key == "selectedDevices") DecodeSelection(value);
                else if (key == "calibrationEnabled") calibrationEnabled = value == "1";
                else if (key == "calibrationBands") calibrationBands = value;
                else if (key == "equalizerPreset") { equalizerPresetName = value; equalizerPresetSeen = true; }
            }
            if (!equalizerPresetSeen && calibrationBands.Split(',').Length == 5)
                equalizerPresetName = "自定义";
            if (string.IsNullOrWhiteSpace(equalizerPresetName)) equalizerPresetName = "自定义";
            calibration.Update(new AudioProfile(calibrationEnabled, DecodeCalibrationBands(calibrationBands), 0.0, 0.0));
            if (!isolatedTest)
                try { StartupEnabled = StartupService.Enabled; } catch { StartupEnabled = false; }
        }
        catch (Exception error) { Log("读取设置失败（忽略）：" + error.Message); RecordFault("设置读取失败", error); }
    }

    private void DecodeSelection(string value)
    {
        selectedKeys.Clear(); selectionOrder.Clear();
        foreach (string token in (value ?? "").Split(','))
        {
            try { string key = Encoding.UTF8.GetString(Convert.FromBase64String(token)); if (key.Length > 0 && selectedKeys.Add(key)) selectionOrder.Add(key); } catch (FormatException) { }
        }
        savedSelection = EncodeSelection();
    }

    private string EncodeSelection()
    {
        return string.Join(",", selectionOrder.Where(selectedKeys.Contains).Select(key => Convert.ToBase64String(Encoding.UTF8.GetBytes(key))));
    }

    private void SaveSettings()
    {
        if (disposed || (isolatedTest && isolatedSettingsDirectory == null)) return;
        try
        {
            string path = SettingsPath();
            File.WriteAllText(path,
                "muteLocalOutput=" + (MuteLocalOutput ? "1" : "0") + Environment.NewLine +
                "autoConnect=" + (AutoConnect ? "1" : "0") + Environment.NewLine +
                "silentStartup=" + (SilentStartup ? "1" : "0") + Environment.NewLine +
                "playbackAutoReconnect=" + (PlaybackAutoReconnect ? "1" : "0") + Environment.NewLine +
                "maxPlaybackReconnectAttempts=" + MaxPlaybackReconnectAttempts + Environment.NewLine +
                "volume=" + Volume + Environment.NewLine + "realtimeVolume=" + (RealtimeVolume ? "1" : "0") + Environment.NewLine +
                "selectedDevices=" + savedSelection + Environment.NewLine + "stereoBalance=" + balancePreference + Environment.NewLine +
                 "latencyMode=" + LatencyProfile.ModeName(LatencyMode) + Environment.NewLine + "latencyCustom=" + CustomLatencyMs + Environment.NewLine +
                 "calibrationEnabled=" + (calibration.Current.Enabled ? "1" : "0") + Environment.NewLine +
                 "equalizerPreset=" + equalizerPresetName + Environment.NewLine +
                 "calibrationBands=" + string.Join(",", Enumerable.Range(0, AudioProfile.BandCount)
                     .Select(index => calibration.Current.BandGainDb(index).ToString("0.0", CultureInfo.InvariantCulture))) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch (Exception error) { try { FaultStore.Default.Record("设置保存失败", error.Message, error.ToString(), ""); } catch { } }
    }

    private string SettingsPath()
    {
        string directory = isolatedSettingsDirectory ?? PackageEnvironment.DataDirectory; Directory.CreateDirectory(directory); return Path.Combine(directory, "settings.txt");
    }

    private static double[] DecodeCalibrationBands(string value)
    {
        string[] parts = (value ?? "").Split(',');
        if (parts.Length == 5)
        {
            double[] legacy = new double[5];
            for (int i = 0; i < legacy.Length; i++)
            {
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
                    legacy[i] = gain;
            }
            return AudioProfile.ExpandLegacyFiveBandGains(legacy);
        }

        double[] gains = new double[AudioProfile.BandCount];
        for (int i = 0; i < gains.Length && i < parts.Length; i++)
        {
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
                gains[i] = gain;
        }
        return gains;
    }
}
