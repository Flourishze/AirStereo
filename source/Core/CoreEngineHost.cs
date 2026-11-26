using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AirStereo2.Models;
using AirStereo2.Services;
using AirStereo2.ViewModels;
using AirStereo2.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AirStereo2;

/// <summary>
/// In-process engine host bridging the WinUI 3 shell with AirStereo2.Engine.exe.
/// Runs inside AirStereo2.exe and talks directly to AirStereo2.Engine.exe (2-process architecture).
/// </summary>
public sealed class CoreEngineHost : IDisposable
{
    public event Action<JsonElement>? MessageReceived;

    private readonly Dispatcher _dispatcher;
    private readonly ServiceProvider _services;
    private readonly HomeViewModel _home;
    private readonly SettingsViewModel _settings;
    private readonly StoreLicenseService _license;
    private readonly ReleaseDiagnosticsService _diagnostics;
    private readonly LocalOutputMuteService _mute;
    private readonly WindowsMediaSessionService _media;
    private readonly AirPlayStreamer _streamer;
    private readonly DispatcherTimer _snapshotTimer;

    private readonly Dictionary<string, object> _targets = new();
    private readonly Queue<string> _logs = new();
    private readonly Dictionary<string, bool> _nativeSupportsAirPlay2 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nativeRaopInstanceName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeRaopAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _publishedRaopAliasFingerprint = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeAirPlay1Keys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AirPlay2OnlyTxtKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ft", "features", "gid", "tsid", "gpn", "igl", "gcgl", "pgid", "pgcgl"
    };

    private static readonly HashSet<string> Setters = new(StringComparer.Ordinal)
    {
        "MasterVolume", "CaptureSourceIndex", "StreamingModeIndex", "CustomBufferSize", "MuteLocalOutputWhileStreaming",
        "ReceiverMediaControlEnabled", "SendNowPlayingMetadata", "AutomaticReceiverRecoveryEnabled", "AudioCodecIndex",
        "SilenceDetectionEnabled", "SilenceTimeoutMinutes", "EqEnabled", "EqPreampDb", "DeveloperModeEnabled",
        "DevSoloLatencyOverrideEnabled", "DevGroupLatencyOverrideEnabled", "DevMixedLatencyOverrideEnabled",
        "DevSoloLatencyText", "DevGroupLatencyText", "DevMixedLatencyText", "SilentStartEnabled", "AutoConnectOnLaunch",
        "LanguageIndex", "DefaultWindowModeIndex", "DockPlacementIndex", "IsDefaultOutput", "Volume", "PinCode", "GainDb"
    };

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "RefreshCommand", "ToggleConnectionCommand", "DisconnectAllCommand", "SubmitPinCommand", "CancelPinCommand",
        "StopWaitingForReclaimCommand", "RefreshStoreLicenseCommand", "CheckNetworkCommand", "FixFirewallCommand",
        "RefreshCaptureDevicesCommand", "RefreshProcessesCommand", "ResetAllDevOverridesCommand", "ResetEqCommand",
        "SaveEqPresetCommand", "DeleteEqPresetCommand", "ExportEqPresetsCommand", "ImportEqPresetsCommand",
        "UploadDiagnosticsCommand", "ReportGlitchCommand", "ResetEqPreampCommand", "ClearPairingsCommand",
        "ExportDiagnosticsCommand", "RunLatencyCalibrationCommand", "OpenWebsiteCommand", "OpenPrivacyPolicyCommand",
        "OpenSupportCommand", "OpenStoreListingCommand", "OpenLogsFolderCommand", "OpenThirdPartyNoticesCommand",
        "ReportIssueCommand"
    };

    private static readonly string[] SettingNames = Setters
        .Except(new[] { "MasterVolume", "Volume", "PinCode", "IsDefaultOutput", "GainDb" })
        .Concat(new[]
        {
            "CurrentLatencyMs", "IsProcessCaptureSupported", "AppVersionText", "BackendStatusText", "BackendPathText",
            "BackendLogPathText", "StoreLicenseStatusText", "StoreLicenseDetailText", "NetworkCheckStatusText",
            "CanFixFirewall", "DefaultOutputSummaryText", "EqPresetStatusText", "DiagnosticsUploadStatusText",
            "SelectedAudioCodec"
        })
        .ToArray();

    private static readonly Regex IsoTimestampRegex = new(
        @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\b",
        RegexOptions.Compiled);

    private volatile bool _stopping;
    private long _revision;
    private int _protocolPreference; // 0 = Auto, 1 = AirPlay 1 (RAOP), 2 = AirPlay 2
    private int _sampleRateHz = 44100;
    private int _masterVolume = 50;
    private string _activeSessionProfile = "";
    private string _activeSessionProtocol = "";
    private string _activeSessionCodec = "";
    private int _activeSessionSampleRateHz;

    // Headless dialog state routed directly to WinUI Shell
    private int _dialogId;
    private readonly Dictionary<int, object?> _dialogValues = new();
    private TaskCompletionSource<string>? _dialogResultTcs;

    public CoreEngineHost()
    {
        MigrateLegacyDataFiles();

        _dispatcher = new Dispatcher();
        Application.Current = new Application(_dispatcher);
        LocalizationManager.Instance.Initialize();

        var sc = new ServiceCollection();
        sc.AddSingleton<AudioCaptureService>();
        sc.AddSingleton<LocalOutputMuteService>();
        sc.AddSingleton<StoreLicenseService>();
        sc.AddSingleton<ReleaseDiagnosticsService>();
        sc.AddSingleton<DiagnosticsUploadService>();
        sc.AddSingleton<CalibrationReportService>();
        sc.AddSingleton<HomeViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddTransient<OnboardingWindow>();
        _services = sc.BuildServiceProvider();
        CoreApp.Services = _services;

        _license = _services.GetRequiredService<StoreLicenseService>();
        Private(_license, "AuthorizeForTesting");
        AirPlayStreamer.ConfigureConnectionAuthorization(_license);

        _diagnostics = _services.GetRequiredService<ReleaseDiagnosticsService>();
        _home = _dispatcher.Invoke(() => _services.GetRequiredService<HomeViewModel>());
        _settings = _dispatcher.Invoke(() => _services.GetRequiredService<SettingsViewModel>());
        _mute = _services.GetRequiredService<LocalOutputMuteService>();
        _media = WindowsMediaSessionService.Instance;
        _streamer = AirPlayStreamer.Instance;
        _masterVolume = SettingsManager.ReadConfig(c => Math.Clamp(c.MasterVolume, 0, 100));

        WireDialogs();

        _streamer.LogReceived += Log;
        _mute.Start();
        _media.Start();
        Log("AirStereo2 1.1.1 音频与设备服务已就绪（AirStereo2.Engine.exe）");

        _snapshotTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _snapshotTimer.Tick += (_, _) => Snapshot();
        _snapshotTimer.Start();

        _dispatcher.InvokeAsync(Snapshot);
    }

    public void PostRequest(JsonElement request)
    {
        if (_stopping) return;
        var clone = request.Clone();
        _dispatcher.BeginInvoke(new Action(() => Handle(clone)));
    }

    public void EmitInitialSnapshot()
    {
        if (_stopping) return;
        _dispatcher.BeginInvoke(new Action(Snapshot));
    }

    private void WireDialogs()
    {
        AppDialogService.OnNoticeAsync = async (_, title, message, ct) =>
        {
            Log($"[{title}] {message}");
            string closeLabel = LocalizationManager.Instance.GetString("Dialog_Close", "Close");
            await ShowWinUiDialogAsync(
                title,
                null,
                closeLabel,
                new object[] { new { type = "text", text = SanitizeDialogText(message) } },
                ct);
        };

        AppDialogService.OnConfirm = async (_, title, message, _, primaryText, closeText, _, ct) =>
        {
            string res = await ShowWinUiDialogAsync(
                title,
                primaryText ?? LocalizationManager.Instance.GetString("Dialog_OK", "OK"),
                closeText ?? LocalizationManager.Instance.GetString("Dialog_Cancel", "Cancel"),
                new object[] { new { type = "text", text = SanitizeDialogText(message) } },
                ct);
            return string.Equals(res, "Primary", StringComparison.OrdinalIgnoreCase);
        };

        AppDialogService.OnPrompt = async (title, message, defaultValue, primaryText, closeText, maxLength, ct) =>
        {
            _dialogValues.Clear();
            _dialogValues[0] = defaultValue ?? "";
            string res = await ShowWinUiDialogAsync(
                title,
                primaryText ?? "确定",
                closeText ?? "取消",
                new object[]
                {
                    new { type = "text", text = SanitizeDialogText(message) },
                    new { type = "input", id = 0, text = defaultValue ?? "", maxLength, multiline = false }
                },
                ct);
            if (!string.Equals(res, "Primary", StringComparison.OrdinalIgnoreCase)) return null;
            return _dialogValues.TryGetValue(0, out var v) ? v?.ToString() ?? "" : "";
        };

        AppDialogService.OnPromptPassword = async (title, message, rememberText, rememberByDefault, primaryText, closeText, errorText, ct) =>
        {
            _dialogValues.Clear();
            _dialogValues[0] = "";
            _dialogValues[1] = rememberByDefault;
            var fields = new List<object> { new { type = "text", text = SanitizeDialogText(message) } };
            if (!string.IsNullOrWhiteSpace(errorText))
                fields.Add(new { type = "text", text = SanitizeDialogText(errorText) });
            fields.Add(new { type = "password", id = 0, maxLength = 128 });
            fields.Add(new { type = "check", id = 1, text = SanitizeDialogText(rememberText), value = rememberByDefault });
            string res = await ShowWinUiDialogAsync(
                title,
                primaryText ?? "确定",
                closeText ?? "取消",
                fields.ToArray(),
                ct);
            if (!string.Equals(res, "Primary", StringComparison.OrdinalIgnoreCase)) return null;
            string pwd = _dialogValues.TryGetValue(0, out var pv) ? pv?.ToString() ?? "" : "";
            bool rem = _dialogValues.TryGetValue(1, out var rv) && rv is bool b && b;
            return (pwd, rem);
        };
    }

    private async Task<string> ShowWinUiDialogAsync(string title, string? primary, string? close, object[] fields, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id = Interlocked.Increment(ref _dialogId);
        _dialogResultTcs = tcs;
        using var reg = ct.Register(() => tcs.TrySetResult("None"));
        Send(new
        {
            kind = "dialog",
            dialogId = id,
            title = SanitizeDialogText(title),
            primary,
            secondary = (string?)null,
            close,
            primaryEnabled = true,
            fields
        });
        string result = await tcs.Task.ConfigureAwait(false);
        Send(new { kind = "dialogClosed", dialogId = id });
        return result;
    }

    private void HandleDialog(JsonElement request)
    {
        string op = request.GetProperty("op").GetString()!;
        if (op == "dialogResult")
        {
            string val = request.GetProperty("value").GetString() ?? "None";
            _dialogResultTcs?.TrySetResult(val);
            return;
        }
        int inputId = int.Parse(request.GetProperty("name").GetString()!, CultureInfo.InvariantCulture);
        var valEl = request.GetProperty("value");
        _dialogValues[inputId] = valEl.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => valEl.GetString() ?? "",
            _ => valEl.ToString()
        };
    }

    private static string SanitizeDialogText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw
            .Replace("实验性 PCM", "PCM", StringComparison.Ordinal)
            .Replace("实验性PCM", "PCM", StringComparison.Ordinal)
            .Replace("实验性：", "", StringComparison.Ordinal)
            .Replace("实验性:", "", StringComparison.Ordinal)
            .Replace("实验性", "", StringComparison.Ordinal)
            .Replace("Experimental PCM", "PCM", StringComparison.Ordinal)
            .Replace("experimental PCM", "PCM", StringComparison.Ordinal)
            .Trim();
    }

    private void Send(object payload)
    {
        if (_stopping) return;
        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var doc = JsonDocument.Parse(json);
            MessageReceived?.Invoke(doc.RootElement.Clone());
        }
        catch { }
    }

    private void Log(string text)
    {
        ObserveBackendSessionLine(text);
        lock (_logs)
        {
            _logs.Enqueue(DateTime.Now.ToString("HH:mm:ss ") + text);
            while (_logs.Count > 500) _logs.Dequeue();
        }
    }

    private void ObserveBackendSessionLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        int sessIdx = text.IndexOf("[SESSION]", StringComparison.Ordinal);
        if (sessIdx >= 0)
        {
            int pIdx = text.IndexOf("profile=", sessIdx, StringComparison.OrdinalIgnoreCase);
            if (pIdx >= 0)
            {
                int start = pIdx + 8;
                int end = text.IndexOf(' ', start);
                string prof = (end > start ? text[start..end] : text[start..]).Trim();
                if (prof.Length > 0)
                {
                    _activeSessionProfile = prof;
                    _activeSessionProtocol = prof.Contains("Raop", StringComparison.OrdinalIgnoreCase) ? "AirPlay 1" : "AirPlay 2";
                }
            }
            int cIdx = text.IndexOf("codec=", sessIdx, StringComparison.OrdinalIgnoreCase);
            if (cIdx >= 0)
            {
                int start = cIdx + 6;
                int end = text.IndexOf(' ', start);
                string cod = (end > start ? text[start..end] : text[start..]).Trim().ToUpperInvariant();
                if (cod == "ALAC" || cod == "PCM") _activeSessionCodec = cod;
            }
            int srIdx = text.IndexOf("sr=", sessIdx, StringComparison.OrdinalIgnoreCase);
            if (srIdx >= 0)
            {
                int start = srIdx + 3;
                int end = text.IndexOf(' ', start);
                string srText = (end > start ? text[start..end] : text[start..]).Trim();
                if (int.TryParse(srText, out int sr) && (sr == 44100 || sr == 48000))
                    _activeSessionSampleRateHz = sr;
            }
            return;
        }
        if (text.IndexOf("Classic RAOP group committed:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _activeSessionProfile = "ClassicRaopGroup";
            _activeSessionProtocol = "AirPlay 1";
            return;
        }
        if (text.Contains("RAOP setup complete", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(_activeSessionProtocol))
        {
            _activeSessionProtocol = "AirPlay 1";
        }
        int codecIdx = text.IndexOf("[Codec] ACTIVE ", StringComparison.Ordinal);
        if (codecIdx >= 0)
        {
            string cod = text[(codecIdx + 15)..].Trim().Split(' ')[0].ToUpperInvariant();
            if (cod == "ALAC" || cod == "PCM") _activeSessionCodec = cod;
        }
    }

    private void Snapshot()
    {
        if (_stopping) return;
        try
        {
            var displayItems = Items(Get(_home, "DisplayItems")).ToArray();
            ApplyProtocolPreference(displayItems);
            bool anyActive = displayItems.Any(d => Equals(Get(d, "IsConnected"), true) || Equals(Get(d, "IsConnecting"), true));
            if (!anyActive)
            {
                _activeSessionProfile = "";
                _activeSessionProtocol = "";
                _activeSessionCodec = "";
                _activeSessionSampleRateHz = 0;
                _activeAirPlay1Keys.Clear();
            }

            var items = displayItems.Select(item =>
            {
                string id = "target-" + RuntimeHelpers.GetHashCode(item);
                _targets[id] = item;
                var result = Scalars(item, new[]
                {
                    "Name", "IPAddress", "Model", "IsConnected", "IsConnecting", "IsFailed", "FailureMessage",
                    "IsStandby", "IsPreempted", "Volume", "IsDefaultOutput", "IsNativeStereoPair", "IsStereoPair",
                    "PairMemberCount", "LatencyText", "ActiveCodecText", "IsPinRequested", "PinCode",
                    "CanEditMembership", "SupportsAirPlay2"
                });
                result["id"] = id;
                result["key"] = TargetKey(item);
                var members = Items(Get(item, "Members")).ToArray();
                var checkList = members.Length > 0 ? members : new[] { item };
                result["HasRaopDiscoveryRecord"] = checkList.All(m => DeviceHasRaopCapability(m, TargetKey(m)));
                result["HasAirPlayDiscoveryRecord"] = checkList.All(m => Equals(GetAny(m, "HasAirPlayDiscoveryRecord"), true));
                result["PreferredBackendHintIsRaop"] = checkList.All(m => Equals(GetAny(m, "PreferredBackendHintIsRaop"), true));
                var defaultEntry = Items(Get(_settings, "DefaultOutputEntries"))
                    .FirstOrDefault(e => ReferenceEquals(Get(e, "Output"), item));
                if (defaultEntry != null) result["IsDefaultOutput"] = Get(defaultEntry, "IsSelected");
                result["members"] = members.Select(m =>
                {
                    var ms = Scalars(m, new[] { "Name", "IPAddress", "Volume", "IsConnected", "SupportsAirPlay2" });
                    ms["PreferredBackendHintIsRaop"] = Equals(GetAny(m, "PreferredBackendHintIsRaop"), true);
                    return ms;
                }).ToArray();
                return result;
            }).ToArray();

            var liveIds = items.Select(i => (string)i["id"]!).ToHashSet();
            foreach (var key in _targets.Keys.Where(k => !liveIds.Contains(k)).ToArray()) _targets.Remove(key);

            string[] logCopy;
            lock (_logs) logCopy = _logs.ToArray();

            var savedDefaults = SettingsManager.GetDefaultOutputDevices().Select(r => new
            {
                stableId = r.StableId ?? "",
                name = r.DisplayName ?? "",
                model = r.Model ?? "",
                ip = r.LastKnownIp ?? ""
            }).ToArray();

            Send(new
            {
                kind = "snapshot",
                revision = ++_revision,
                scanning = _home.IsScanning,
                masterVolume = _masterVolume,
                devices = items,
                defaultOutputs = savedDefaults,
                settings = Scalars(_settings, SettingNames),
                logs = logCopy,
                calibration = new { open = false },
                protocolMode = _protocolPreference,
                activeProtocol = _activeSessionProtocol,
                activeCodec = _activeSessionCodec,
                activeProfile = _activeSessionProfile,
                sampleRateHz = _sampleRateHz,
                activeSampleRateHz = _activeSessionSampleRateHz,
                commandAvailable = Commands
                    .Where(n => _settings.GetType().GetProperty(n) != null)
                    .ToDictionary(n => n, n => (Get(_settings, n) as ICommand)?.CanExecute(null) == true),
                captureDevices = Items(Get(_settings, "AvailableCaptureDevices"))
                    .Select((x, i) => new { index = i, name = Get(x, "Name") ?? x.ToString() }).ToArray(),
                processes = Items(Get(_settings, "AvailableProcesses"))
                    .Select((x, i) => new { index = i, name = Get(x, "DisplayName") ?? Get(x, "Name") ?? x.ToString() }).ToArray(),
                selectedCaptureDevice = Items(Get(_settings, "AvailableCaptureDevices")).ToList().IndexOf(Get(_settings, "SelectedCaptureDevice")!),
                selectedProcess = Items(Get(_settings, "AvailableProcesses")).ToList().IndexOf(Get(_settings, "SelectedProcess")!),
                eqBands = Items(Get(_settings, "EqBands"))
                    .Select((x, i) => new { index = i, name = Get(x, "Label") ?? Get(x, "FrequencyLabel") ?? Get(x, "FrequencyText") ?? (i + 1).ToString(), gain = Get(x, "GainDb") ?? Get(x, "Gain") }).ToArray(),
                eqPresets = Items(Get(_settings, "EqPresets"))
                    .Select((x, i) => new { index = i, name = Get(x, "Name") ?? Get(x, "DisplayName") ?? x.ToString() }).ToArray(),
                selectedEqPreset = Items(Get(_settings, "EqPresets")).ToList().IndexOf(Get(_settings, "SelectedEqPreset")!)
            });
        }
        catch (Exception ex)
        {
            Log("读取状态失败: " + Unwrap(ex).Message);
        }
    }

    private async void Handle(JsonElement request)
    {
        int id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
        try
        {
            string op = request.GetProperty("op").GetString()!;
            string targetName = request.TryGetProperty("target", out var t) ? t.GetString() ?? "settings" : "settings";
            if (op == "quit") { Dispose(); return; }
            if (op == "dialogInput" || op == "dialogResult")
            {
                HandleDialog(request);
                Send(new { kind = "reply", id, ok = true });
                return;
            }
            if (op == "initialize")
            {
                Private(_license, "AuthorizeForTesting");
            }
            else if (op == "startup")
            {
                bool startupLaunch = request.GetProperty("value").GetBoolean();
                var defaults = SettingsManager.GetDefaultOutputDevices();
                if (defaults.Count > 0 && StartupAutoConnectPolicy.ShouldRun(startupLaunch, _settings.AutoConnectOnLaunch, defaults.Count))
                {
                    Log($"启动自动连接默认音响（共 {defaults.Count} 个目标）");
                    var coordinator = new SilentStartupCoordinator(
                        () => SettingsManager.GetDefaultOutputDevices(),
                        () => _home.Devices.ToArray(),
                        async dev =>
                        {
                            if (!dev.IsConnected && !dev.IsConnecting && dev.ToggleConnectionCommand.CanExecute(null))
                            {
                                PushSampleRateToBackend();
                                await dev.ToggleConnectionCommand.ExecuteAsync(null);
                            }
                        });
                    var report = await coordinator.RunAsync(CancellationToken.None);
                    if (report != null && report.Kind != SilentStartupOutcomeKind.AllSucceeded)
                    {
                        var retry = await coordinator.RetryUnresolvedAsync(report, CancellationToken.None);
                        if (retry != null) report = retry;
                    }
                    Log("默认音响自动连接结果: " + (report?.Kind.ToString() ?? "Unknown"));
                }
            }
            else if (op == "setProtocol")
            {
                _protocolPreference = Math.Clamp(request.GetProperty("value").GetInt32(), 0, 2);
                ApplyProtocolPreference(Items(Get(_home, "DisplayItems")).ToArray());
                string label = _protocolPreference switch { 1 => "AirPlay 1 (RAOP)", 2 => "AirPlay 2", _ => "自动推荐" };
                Log("连接协议偏好已切换为：" + label);
            }
            else if (op == "setSampleRate")
            {
                int hz = request.GetProperty("value").GetInt32();
                _sampleRateHz = hz == 48000 ? 48000 : 44100;
                PushSampleRateToBackend();
                Log($"输出采样率偏好已切换为：{_sampleRateHz} Hz");
            }
            else if (op == "removeDefaultOutput")
            {
                string stableId = request.GetProperty("value").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(stableId))
                {
                    var existing = SettingsManager.GetDefaultOutputDevices()
                        .FirstOrDefault(r => string.Equals(r.StableId, stableId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        SettingsManager.SetDefaultOutput(existing, false);
                        SettingsManager.FlushPendingSave();
                        try { Private(_settings, "RefreshDefaultOutputEntries"); } catch { }
                    }
                }
            }
            else if (op == "exportDiagnostics")
            {
                bool english = ReadLanguageIsEnglish(request);
                string targetPath = request.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(targetPath))
                    throw new InvalidOperationException("未指定导出路径");

                Private(_license, "AuthorizeForTesting");
                var licenseSnapshot = _license.Current;
                string? summary = await _diagnostics.CollectSessionSummaryAsync(CancellationToken.None);
                string writtenPath = _diagnostics.WriteReport(licenseSnapshot, targetPath, summary);
                string report = File.ReadAllText(writtenPath, Encoding.UTF8);
                report = SanitizeDiagnosticsReport(report, english);
                File.WriteAllText(writtenPath, report, new UTF8Encoding(false));
                Send(new { kind = "reply", id, ok = true, path = writtenPath });
                Snapshot();
                return;
            }
            else if (op == "calibration")
            {
                // Headless no-op for legacy WPF calibration wizard mirror
            }
            else if (op == "activate")
            {
                _settings.ActivateTransientCaptureCatalogs();
            }
            else if (op == "deactivate")
            {
                _settings.ReleaseTransientCaptureCatalogs();
            }
            else if (op == "exportPresets")
            {
                string json = SettingsManager.ExportEqPresetsJson(true);
                File.WriteAllText(request.GetProperty("value").GetString()!, json);
            }
            else if (op == "importPresets")
            {
                string path = request.GetProperty("value").GetString()!;
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("预设文件过大");
                var result = SettingsManager.ImportEqPresetsJson(File.ReadAllText(path));
                if (!result.Success) throw new InvalidOperationException(result.Message);
                Private(_settings, "RebuildEqPresetList", true);
                Private(_settings, "UpdateEqPresetSelectionFromGains");
            }
            else if (op == "set")
            {
                string name = request.GetProperty("name").GetString()!;
                if (!Setters.Contains(name)) throw new InvalidOperationException("不支持该设置");
                if (targetName == "main" && name == "MasterVolume")
                {
                    int vol = Math.Clamp(request.GetProperty("value").GetInt32(), 0, 100);
                    _masterVolume = vol;
                    SettingsManager.UpdateConfig(c => c.MasterVolume = vol);
                    foreach (var d in _home.Devices)
                    {
                        if (d.IsConnected) d.Volume = vol;
                    }
                }
                else
                {
                    object target = ResolveTarget(targetName);
                    if (name == "IsDefaultOutput")
                    {
                        var entry = Items(Get(_settings, "DefaultOutputEntries"))
                            .FirstOrDefault(e => ReferenceEquals(Get(e, "Output"), target));
                        if (entry == null)
                        {
                            try { Private(_settings, "RefreshDefaultOutputEntries"); } catch { }
                            entry = Items(Get(_settings, "DefaultOutputEntries"))
                                .FirstOrDefault(e => ReferenceEquals(Get(e, "Output"), target));
                        }
                        target = entry ?? throw new InvalidOperationException("默认目标已离线");
                        name = "IsSelected";
                    }
                    var p = target.GetType().GetProperty(name) ?? throw new InvalidOperationException("不支持此配置项");
                    if (!p.CanWrite) throw new InvalidOperationException("该状态只读");
                    p.SetValue(target, request.GetProperty("value").Deserialize(p.PropertyType));
                    if (name == "AudioCodecIndex")
                    {
                        bool anyActiveNow = Items(Get(_home, "DisplayItems"))
                            .Any(d => Equals(Get(d, "IsConnected"), true) || Equals(Get(d, "IsConnecting"), true));
                        if (!anyActiveNow) _activeSessionCodec = "";
                    }
                    if (name == "IsSelected" || name == "AutoConnectOnLaunch")
                        SettingsManager.FlushPendingSave();
                }
            }
            else if (op == "select")
            {
                string name = request.GetProperty("name").GetString()!;
                string collection = name switch
                {
                    "SelectedCaptureDevice" => "AvailableCaptureDevices",
                    "SelectedProcess" => "AvailableProcesses",
                    "SelectedEqPreset" => "EqPresets",
                    _ => throw new InvalidOperationException("无效选项")
                };
                var value = Items(Get(_settings, collection)).ElementAtOrDefault(request.GetProperty("value").GetInt32());
                _settings.GetType().GetProperty(name)!.SetValue(_settings, value);
            }
            else if (op == "eq")
            {
                var band = Items(Get(_settings, "EqBands")).ElementAt(request.GetProperty("index").GetInt32());
                var p = band.GetType().GetProperty("GainDb") ?? band.GetType().GetProperty("Gain");
                p!.SetValue(band, request.GetProperty("value").Deserialize(p.PropertyType));
            }
            else if (op == "savePreset")
            {
                string name = EqLayout.SanitizePresetName(request.GetProperty("value").GetString());
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("预设名称不能为空");
                Private(_settings, "FlushPendingEqBandsNow");
                double[] gains = Items(Get(_settings, "EqBands")).Select(b => Convert.ToDouble(Get(b, "GainDb"))).ToArray();
                SettingsManager.SaveUserPreset(name, gains);
                Private(_settings, "RebuildEqPresetList", false);
                Private(_settings, "SelectPresetById", EqPresetItem.UserId(name));
            }
            else if (op == "deletePreset")
            {
                var preset = _settings.SelectedEqPreset;
                if (preset == null || !preset.IsUser) throw new InvalidOperationException("只能删除自定义预设");
                SettingsManager.DeleteUserPreset(preset.DisplayName);
                Private(_settings, "RebuildEqPresetList", false);
                Private(_settings, "UpdateEqPresetSelectionFromGains");
            }
            else if (op == "command")
            {
                string name = request.GetProperty("name").GetString()!;
                if (!Commands.Contains(name)) throw new InvalidOperationException("不支持此命令");
                if (name == "DisconnectAllCommand")
                {
                    DisconnectAll();
                }
                else
                {
                    object target = ResolveTarget(targetName);
                    if (name == "ToggleConnectionCommand")
                    {
                        PushSampleRateToBackend();
                        ApplyProtocolPreference(Items(Get(_home, "DisplayItems")).ToArray());
                        bool targetActive = Equals(Get(target, "IsConnected"), true) || Equals(Get(target, "IsConnecting"), true);
                        if (!targetActive && _protocolPreference == 1)
                        {
                            var members = Items(Get(target, "Members")).ToArray();
                            var checkList = members.Length > 0 ? members : new[] { target };
                            bool allSupportRaop = checkList.All(dev =>
                            {
                                string k = TargetKey(dev);
                                if (string.IsNullOrEmpty(k)) k = Get(dev, "IPAddress")?.ToString() ?? "";
                                return DeviceHasRaopCapability(dev, k);
                            });
                            if (!allSupportRaop)
                                throw new InvalidOperationException("该音响不支持 AirPlay 1 (RAOP)，请在「设置 → 播放与音频」切换为自动或 AirPlay 2 模式。");
                            foreach (var dev in checkList)
                            {
                                string k = TargetKey(dev);
                                if (string.IsNullOrEmpty(k)) k = Get(dev, "IPAddress")?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(k)) _activeAirPlay1Keys.Add(k);
                            }
                            ApplyProtocolPreference(Items(Get(_home, "DisplayItems")).ToArray());
                        }
                        else if (!targetActive && _protocolPreference == 2)
                        {
                            var members = Items(Get(target, "Members")).ToArray();
                            var checkList = members.Length > 0 ? members : new[] { target };
                            bool allSupportAp2 = checkList.All(dev =>
                            {
                                string k = TargetKey(dev);
                                return _nativeSupportsAirPlay2.TryGetValue(k, out bool n) ? n : Equals(Get(dev, "SupportsAirPlay2"), true);
                            });
                            if (!allSupportAp2)
                                throw new InvalidOperationException("该音响仅支持 AirPlay 1，请在「设置 → 播放与音频」切换为自动或 AirPlay 1 模式。");
                        }
                    }

                    var command = Get(target, name) as ICommand ?? throw new InvalidOperationException("当前命令不可用");
                    if (!command.CanExecute(null)) throw new InvalidOperationException("当前状态下不可执行");
                    var asyncMethod = command.GetType().GetMethod("ExecuteAsync", new[] { typeof(object) });
                    if (asyncMethod != null) await (Task)asyncMethod.Invoke(command, new object?[] { null })!;
                    else command.Execute(null);
                }
            }
            else if (op != "snapshot")
            {
                throw new InvalidOperationException("未知界面请求");
            }

            Send(new { kind = "reply", id, ok = true });
            Snapshot();
        }
        catch (Exception ex)
        {
            string error = Unwrap(ex).Message;
            Log(error);
            Send(new { kind = "reply", id, ok = false, error });
        }
    }

    private void DisconnectAll()
    {
        try
        {
            foreach (var item in Items(Get(_home, "DisplayItems")).ToArray())
            {
                if (Equals(Get(item, "IsConnected"), true) || Equals(Get(item, "IsConnecting"), true))
                {
                    if (Get(item, "ToggleConnectionCommand") is ICommand cmd && cmd.CanExecute(null))
                        cmd.Execute(null);
                }
            }
            _streamer.RemoveAllDevices();
        }
        catch (Exception ex)
        {
            Log("断开全部设备失败: " + Unwrap(ex).Message);
        }
    }

    private object ResolveTarget(string name) => name switch
    {
        "home" => _home,
        "settings" => _settings,
        string key when _targets.TryGetValue(key, out var obj) => obj,
        _ => throw new InvalidOperationException("目标已离线")
    };

    private void PushSampleRateToBackend()
    {
        try
        {
            _streamer.SetSampleRate(_sampleRateHz);
        }
        catch { }
    }

    private bool DeviceHasRaopCapability(object dev, string key)
    {
        // HomePod / HomePod mini (AudioAccessory*) only accept encrypted AirPlay 2;
        // their _raop._tcp advertisement is AirPlay 2 HAP-encrypted, never classic unencrypted AirPlay 1.
        string model = GetAny(dev, "Model")?.ToString()?.Trim() ?? "";
        if (model.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Equals(GetAny(dev, "HasRaopDiscoveryRecord"), true)) return true;
        if (!string.IsNullOrWhiteSpace(GetAny(dev, "RaopInstanceName")?.ToString())) return true;
        if (_nativeRaopInstanceName.TryGetValue(key, out string? nr) && !string.IsNullOrWhiteSpace(nr)) return true;
        return false;
    }

    private IEnumerable<object> CollectAllDeviceModels(object[] displayItems)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in displayItems)
        {
            var members = Items(Get(item, "Members")).ToArray();
            if (members.Length > 0)
            {
                foreach (var m in members)
                    if (seen.Add(m)) yield return m;
            }
            else if (seen.Add(item))
            {
                yield return item;
            }
        }
        foreach (var d in _home.Devices)
            if (seen.Add(d)) yield return d;
    }

    private void ApplyProtocolPreference(object[] displayItems)
    {
        foreach (var dev in CollectAllDeviceModels(displayItems))
        {
            var ap2Prop = dev.GetType().GetProperty("SupportsAirPlay2");
            if (ap2Prop == null || !ap2Prop.CanWrite) continue;
            string key = TargetKey(dev);
            if (string.IsNullOrEmpty(key)) key = Get(dev, "IPAddress")?.ToString() ?? "";
            if (string.IsNullOrEmpty(key)) continue;

            bool currentAp2 = Equals(ap2Prop.GetValue(dev), true);
            string currentRaop = GetAny(dev, "RaopInstanceName")?.ToString() ?? "";
            bool isOurAlias = _activeRaopAlias.TryGetValue(key, out string? knownAlias)
                && string.Equals(currentRaop, knownAlias, StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(currentRaop) && !isOurAlias)
                _nativeRaopInstanceName[key] = currentRaop;

            if (!_nativeSupportsAirPlay2.TryGetValue(key, out bool nativeAp2))
            {
                nativeAp2 = currentAp2;
                _nativeSupportsAirPlay2[key] = nativeAp2;
            }
            else if (_protocolPreference != 1 && currentAp2 && !nativeAp2)
            {
                nativeAp2 = true;
                _nativeSupportsAirPlay2[key] = true;
            }

            bool isActive = Equals(Get(dev, "IsConnected"), true) || Equals(Get(dev, "IsConnecting"), true);
            if (!isActive) _activeAirPlay1Keys.Remove(key);

            if (_protocolPreference == 1)
            {
                if (isActive && !_activeAirPlay1Keys.Contains(key)) continue;
                string realRaop = _nativeRaopInstanceName.TryGetValue(key, out string? nr) ? nr : (!isOurAlias ? currentRaop : "");
                bool hasRaop = !string.IsNullOrWhiteSpace(realRaop) || Equals(GetAny(dev, "HasRaopDiscoveryRecord"), true);
                if (!hasRaop) continue;

                if (nativeAp2)
                {
                    string alias = BuildRaopOnlyAlias(key, realRaop, Get(dev, "Name")?.ToString() ?? "");
                    _activeRaopAlias[key] = alias;
                    EnsureRaopOnlyDeviceHint(dev, key, realRaop, alias);
                    if (!string.Equals(currentRaop, alias, StringComparison.Ordinal))
                    {
                        try { SetAny(dev, "RaopInstanceName", alias); } catch { }
                    }
                }
                if (currentAp2)
                {
                    try { ap2Prop.SetValue(dev, false); } catch { }
                }
            }
            else
            {
                if (isActive) continue;
                if (_nativeRaopInstanceName.TryGetValue(key, out string? realRaop)
                    && !string.IsNullOrWhiteSpace(realRaop)
                    && !string.Equals(currentRaop, realRaop, StringComparison.Ordinal))
                {
                    try { SetAny(dev, "RaopInstanceName", realRaop); } catch { }
                }
                if (currentAp2 != nativeAp2)
                {
                    try { ap2Prop.SetValue(dev, nativeAp2); } catch { }
                }
            }
        }
    }

    private static string BuildRaopOnlyAlias(string key, string realRaopInstance, string fallbackName)
    {
        string display = fallbackName;
        int at = realRaopInstance.IndexOf('@');
        if (at >= 0 && at + 1 < realRaopInstance.Length)
            display = realRaopInstance[(at + 1)..];
        if (string.IsNullOrWhiteSpace(display)) display = "Speaker";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + realRaopInstance));
        return "A1" + Convert.ToHexString(hash, 0, 5) + "@" + display.Trim();
    }

    private void EnsureRaopOnlyDeviceHint(object dev, string key, string realRaopInstance, string alias)
    {
        string ip = Get(dev, "IPAddress")?.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(ip)) return;
        string[] addresses = new[] { ip };
        int port = Get(dev, "Port") is int dp && dp > 0 ? dp : 7000;
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cn"] = "0,1",
            ["et"] = "0,3,5",
            ["tp"] = "UDP"
        };
        string model = Get(dev, "Model")?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(model)) txt["am"] = model;
        string fingerprint = alias + "|" + ip + "|" + port + "|" + model;
        if (_publishedRaopAliasFingerprint.TryGetValue(key, out string? prev) && prev == fingerprint) return;
        try
        {
            _streamer.PublishDeviceHint(new BackendDeviceHint(alias, addresses, port, true, txt));
            _publishedRaopAliasFingerprint[key] = fingerprint;
        }
        catch (Exception ex)
        {
            Log("发布 AirPlay 1 兼容描述符失败: " + Unwrap(ex).Message);
        }
    }

    private static bool ReadLanguageIsEnglish(JsonElement request)
    {
        if (request.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            string s = n.GetString()?.Trim() ?? "";
            if (s.Equals("en-US", StringComparison.OrdinalIgnoreCase) || s.Equals("en", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || s.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        try
        {
            string langFile = Path.Combine(AppContext.BaseDirectory, "Data", "Interface", "language.txt");
            if (File.Exists(langFile))
                return File.ReadAllText(langFile).Trim().Equals("en-US", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return false;
    }

    private static string ConvertTimestampsForLanguage(string text, bool english)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var cst = TimeSpan.FromHours(8);
        return IsoTimestampRegex.Replace(text, m =>
        {
            if (!DateTimeOffset.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                return m.Value;
            int dot = m.Value.IndexOf('.');
            int fracLen = 0;
            if (dot >= 0)
            {
                int end = dot + 1;
                while (end < m.Value.Length && char.IsDigit(m.Value[end])) end++;
                fracLen = Math.Min(end - dot - 1, 6);
            }
            string frac = fracLen > 0 ? "." + new string('f', fracLen) : "";
            return english
                ? dto.ToUniversalTime().ToString($"yyyy-MM-dd'T'HH:mm:ss{frac}'Z'", CultureInfo.InvariantCulture)
                : dto.ToOffset(cst).ToString($"yyyy-MM-dd HH:mm:ss{frac} '+08:00'", CultureInfo.InvariantCulture);
        });
    }

    private string SanitizeDiagnosticsReport(string report, bool english)
    {
        var lines = report.Replace("\r\n", "\n").Split('\n');
        var cleaned = new List<string>(lines.Length + 12);
        string prefLabel = _protocolPreference switch { 1 => "AirPlay 1 (RAOP)", 2 => "AirPlay 2", _ => "Auto" };
        cleaned.Add("[AirStereo2 Summary]");
        cleaned.Add($"Protocol Preference: {prefLabel}");
        cleaned.Add($"Output Sample Rate: {_sampleRateHz} Hz");
        cleaned.Add($"Log Timezone: {(english ? "UTC (en-US)" : "北京时间 UTC+08:00 (zh-CN)")}");
        if (!string.IsNullOrEmpty(_activeSessionProtocol) || !string.IsNullOrEmpty(_activeSessionProfile))
            cleaned.Add($"Active Session: {_activeSessionProtocol} / {_activeSessionCodec} (profile={_activeSessionProfile})");
        try
        {
            foreach (var d in Items(Get(_home, "DisplayItems")).ToArray())
            {
                string dName = Get(d, "Name")?.ToString() ?? "";
                var members = Items(Get(d, "Members")).ToArray();
                var checkList = members.Length > 0 ? members : new[] { d };
                string dModel = Get(d, "Model")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(dModel))
                    dModel = string.Join(",", checkList.Select(m => Get(m, "Model")?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                string dIp = Get(d, "IPAddress")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(dIp))
                    dIp = string.Join(",", checkList.Select(m => Get(m, "IPAddress")?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                bool ap2 = checkList.All(m =>
                {
                    string mk = TargetKey(m);
                    return _nativeSupportsAirPlay2.TryGetValue(mk, out bool n) ? n : Equals(Get(m, "SupportsAirPlay2"), true);
                });
                bool raop = checkList.All(m => DeviceHasRaopCapability(m, TargetKey(m)));
                cleaned.Add($"Device: {dName} | Model={dModel} | IP={dIp} | AirPlay2={ap2} | RAOP={raop}");
            }
        }
        catch { }
        cleaned.Add("");
        bool licenseReplaced = false;
        foreach (string raw in lines)
        {
            string line = raw;
            if (line.StartsWith("App version: ", StringComparison.Ordinal) && line.EndsWith(".0", StringComparison.Ordinal))
                line = line[..^2];
            if (line.StartsWith("License ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Store ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase))
            {
                if (!licenseReplaced)
                {
                    cleaned.Add("Distribution: MIT Open Source (https://github.com/Flourishze/AirStereo)");
                    licenseReplaced = true;
                }
                continue;
            }
            cleaned.Add(ConvertTimestampsForLanguage(line, english));
        }
        return string.Join(Environment.NewLine, cleaned);
    }

    private static void MigrateLegacyDataFiles()
    {
        try
        {
            string dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            if (Directory.Exists(dataDir))
            {
                string oldCfg = Path.Combine(dataDir, "app_settings.json");
                string newCfg = Path.Combine(dataDir, "user_options.json");
                if (File.Exists(oldCfg) && !File.Exists(newCfg)) File.Move(oldCfg, newCfg);
                else if (File.Exists(oldCfg)) File.Delete(oldCfg);
            }
        }
        catch { }
    }

    private static string TargetKey(object item) =>
        (Get(item, "StableId") ?? string.Join("|", Items(Get(item, "Members")).Select(m => Get(m, "StableId")?.ToString()).OrderBy(n => n))).ToString() ?? "";

    private static object? Get(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target);
    private static object? GetAny(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    private static void SetAny(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) prop.SetValue(target, value);
    }
    private static IEnumerable<object> Items(object? value) => value is IEnumerable list ? list.Cast<object>() : Array.Empty<object>();
    private static Dictionary<string, object?> Scalars(object target, IEnumerable<string> names) => names.ToDictionary(n => n, n => Get(target, n));
    private static object? Private(object target, string method, params object?[] args) =>
        (target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(method)).Invoke(target, args);
    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? Unwrap(tie.InnerException!) : ex;

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        try { _snapshotTimer.Stop(); } catch { }
        try
        {
            _dispatcher.Invoke(() =>
            {
                try { DisconnectAll(); } catch { }
                try { _home.Dispose(); } catch { }
                try { Private(_settings, "FlushPendingEqBandsNow"); } catch { }
                try { (_settings as IDisposable)?.Dispose(); } catch { }
                try { _mute.Dispose(); } catch { }
                try { _media.Dispose(); } catch { }
                try { AirPlayStreamer.DisposeIfCreated(); } catch { }
                try { SettingsManager.FlushPendingSave(); } catch { }
            });
        }
        catch { }
        try { _services.Dispose(); } catch { }
        try { _dispatcher.Dispose(); } catch { }
    }
}

