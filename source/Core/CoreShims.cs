// Minimal headless WPF-compatible shims so AirStereo2.Core runs 100% pure-source in-process
// inside WinUI 3 AirStereo2.exe without any WPF runtime assemblies (PresentationCore/Framework/Wpf.Ui).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows
{
    public enum Visibility
    {
        Visible = 0,
        Hidden = 1,
        Collapsed = 2
    }

    public readonly struct Int32Rect
    {
        public static readonly Int32Rect Empty = default;
    }

    public abstract class DependencyObject : System.Windows.Threading.DispatcherObject { }

    public abstract class Freezable : DependencyObject
    {
        public bool CanFreeze => true;
        public void Freeze() { }
    }

    public sealed class Application : System.Windows.Threading.DispatcherObject
    {
        public static Application? Current { get; set; }

        public Application(System.Windows.Threading.Dispatcher? dispatcher = null)
        {
            Dispatcher = dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        }

        public new System.Windows.Threading.Dispatcher Dispatcher { get; }
        public void Shutdown() { }
    }

    public static class Clipboard
    {
        public static void SetText(string text) { }
    }

    public class Window : DependencyObject
    {
        public bool Topmost { get; set; }
        public event EventHandler? Closed;
        public void Show() { Closed?.Invoke(this, EventArgs.Empty); }
        public void Activate() { }
        public void Close() { Closed?.Invoke(this, EventArgs.Empty); }
    }
}

namespace Microsoft.Win32
{
    public class FileDialog
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public string DefaultExt { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool AddExtension { get; set; }
        public bool OverwritePrompt { get; set; }
        public bool CheckFileExists { get; set; }
        public bool? ShowDialog() => false;
    }
    public sealed class SaveFileDialog : FileDialog { }
    public sealed class OpenFileDialog : FileDialog
    {
        public bool Multiselect { get; set; }
    }
}

namespace AirStereo2
{
    public static class CoreApp
    {
        public static IServiceProvider? Services { get; set; }
        public static T GetRequiredService<T>() where T : notnull
            => (T)(Services?.GetService(typeof(T)) ?? throw new InvalidOperationException($"Service not registered: {typeof(T).Name}"));
        public static T? GetService<T>() where T : class
            => Services?.GetService(typeof(T)) as T;
    }
}

namespace AirStereo2.Views
{
    public sealed class OnboardingWindow : System.Windows.Window { }
}

namespace AirStereo2.Views.Dialogs
{
    public static class SimpleTextInputDialog
    {
        public static Task<string?> PromptAsync(string title, string message, string defaultValue = "", int maxLength = 0)
            => AirStereo2.Services.AppDialogService.Instance.PromptAsync(title, message, defaultValue, null, null, maxLength);

        public static Task<string?> PromptAsync(string title, string message, string defaultValue, string? primaryText, string? closeText, int maxLength = 0)
            => AirStereo2.Services.AppDialogService.Instance.PromptAsync(title, message, defaultValue, primaryText, closeText, maxLength);
    }
}

namespace System.Windows.Threading
{
    public enum DispatcherPriority
    {
        Invalid = -1,
        Inactive = 0,
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10
    }

    public class DispatcherObject
    {
        public Dispatcher Dispatcher => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public sealed class DispatcherOperation
    {
        public Task Task { get; }
        public DispatcherOperation(Task task) => Task = task;
        public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => Task.GetAwaiter();
    }

    public sealed class Dispatcher : IDisposable
    {
        public static Dispatcher CurrentDispatcher { get; private set; } = new Dispatcher();

        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private volatile bool _stopped;

        public Dispatcher()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "AirStereo2.Core.Dispatcher"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            CurrentDispatcher = this;
        }

        private void RunLoop()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); } catch { }
            }
        }

        public bool CheckAccess() => Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId;
        public bool HasShutdownStarted => _stopped;
        public bool HasShutdownFinished => _stopped;

        public void Invoke(Action callback, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (callback == null) return;
            if (CheckAccess() || _stopped) { callback(); return; }
            using var done = new ManualResetEventSlim(false);
            Exception? captured = null;
            _queue.Add(() =>
            {
                try { callback(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (captured != null) throw captured;
        }

        public T Invoke<T>(Func<T> callback, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (CheckAccess() || _stopped) return callback();
            T result = default!;
            Invoke(() => { result = callback(); }, priority);
            return result;
        }

        public DispatcherOperation InvokeAsync(Action callback, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (callback == null) return new DispatcherOperation(Task.CompletedTask);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_stopped)
            {
                try { callback(); tcs.TrySetResult(); } catch (Exception ex) { tcs.TrySetException(ex); }
                return new DispatcherOperation(tcs.Task);
            }
            _queue.Add(() =>
            {
                try { callback(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return new DispatcherOperation(tcs.Task);
        }

        public DispatcherOperation BeginInvoke(Action callback, DispatcherPriority priority = DispatcherPriority.Normal)
            => InvokeAsync(callback, priority);

        public DispatcherOperation BeginInvoke(Delegate method, params object?[] args)
            => InvokeAsync(() => method.DynamicInvoke(args), DispatcherPriority.Normal);

        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method, params object?[] args)
            => InvokeAsync(() => method.DynamicInvoke(args), priority);

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            _queue.CompleteAdding();
        }
    }

    public sealed class DispatcherTimer
    {
        private readonly Dispatcher _dispatcher;
        private Timer? _timer;
        private TimeSpan _interval;
        private bool _running;

        public event EventHandler? Tick;
        public TimeSpan Interval
        {
            get => _interval;
            set
            {
                _interval = value;
                if (_running && _timer != null)
                {
                    int ms = Math.Max(1, (int)_interval.TotalMilliseconds);
                    _timer.Change(ms, ms);
                }
            }
        }
        public bool IsEnabled
        {
            get => _running;
            set { if (value) Start(); else Stop(); }
        }

        public DispatcherTimer() : this(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { }
        public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        }
        public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler callback, Dispatcher dispatcher)
            : this(priority, dispatcher)
        {
            _interval = interval;
            if (callback != null) Tick += callback;
            Start();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            int ms = Math.Max(1, (int)_interval.TotalMilliseconds);
            _timer = new Timer(_ =>
            {
                if (!_running) return;
                _dispatcher.InvokeAsync(() =>
                {
                    if (_running) Tick?.Invoke(this, EventArgs.Empty);
                });
            }, null, ms, ms);
        }

        public void Stop()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
        public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
    }

    public abstract class Brush : System.Windows.Freezable { }
    public sealed class SolidColorBrush : Brush
    {
        public Color Color { get; set; }
        public SolidColorBrush() { }
        public SolidColorBrush(Color color) { Color = color; }
    }
    public abstract class ImageSource : System.Windows.Freezable { }
    public enum FillRule { EvenOdd, Nonzero }
    public abstract class Geometry : System.Windows.Freezable
    {
        public static Geometry Parse(string source) => new PathGeometry();
    }
    public sealed class PathGeometry : Geometry { }
    public sealed class GeometryGroup : Geometry
    {
        public FillRule FillRule { get; set; }
        public List<Geometry> Children { get; } = new();
    }
}

namespace System.Windows.Media.Imaging
{
    public enum BitmapCacheOption { Default, OnDemand, OnLoad, None }
    [Flags]
    public enum BitmapCreateOptions { None = 0, PreservePixelFormat = 1, DelayCreation = 2, IgnoreColorProfile = 4, IgnoreImageCache = 8 }
    public abstract class BitmapSource : System.Windows.Media.ImageSource
    {
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
    }
    public sealed class BitmapSizeOptions
    {
        public static BitmapSizeOptions FromEmptyOptions() => new();
        public static BitmapSizeOptions FromWidthAndHeight(int pixelWidth, int pixelHeight) => new();
    }
    public sealed class BitmapImage : BitmapSource
    {
        public Uri? UriSource { get; set; }
        public Stream? StreamSource { get; set; }
        public BitmapCacheOption CacheOption { get; set; }
        public BitmapCreateOptions CreateOptions { get; set; }
        public int DecodePixelWidth { get; set; }
        public int DecodePixelHeight { get; set; }
        public void BeginInit() { }
        public void EndInit() { }
    }
    public sealed class BitmapFrame : BitmapSource
    {
        public static BitmapFrame Create(BitmapSource source) => new();
    }
    public sealed class BitmapDecoder
    {
        public List<BitmapFrame> Frames { get; } = new() { new BitmapFrame() };
        public static BitmapDecoder Create(Stream bitmapStream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) => new();
    }
    public sealed class JpegBitmapEncoder
    {
        public int QualityLevel { get; set; } = 90;
        public List<BitmapFrame> Frames { get; } = new();
        public void Save(Stream stream) { }
    }
}

namespace System.Windows.Interop
{
    public static class Imaging
    {
        public static System.Windows.Media.Imaging.BitmapSource CreateBitmapSourceFromHIcon(
            IntPtr icon, System.Windows.Int32Rect sourceRect, System.Windows.Media.Imaging.BitmapSizeOptions sizeOptions)
            => new System.Windows.Media.Imaging.BitmapImage();
    }
}

namespace AirStereo2.Services
{
    public sealed class AppDialogService : IAppDialogService
    {
        public static AppDialogService Instance { get; } = new();

        public static Func<string, string, string, string, string?, string?, bool, CancellationToken, Task<bool>>? OnConfirm;
        public static Func<string, string, string, string?, string?, int, CancellationToken, Task<string?>>? OnPrompt;
        public static Func<string, string, string, bool, string?, string?, string?, CancellationToken, Task<(string Password, bool Remember)?>>? OnPromptPassword;
        public static Func<string, string, string, CancellationToken, Task>? OnNoticeAsync;

        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return OnNoticeAsync != null ? OnNoticeAsync("info", title, message, cancellationToken) : Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return OnNoticeAsync != null ? OnNoticeAsync("error", title, message, cancellationToken) : Task.CompletedTask;
        }

        public Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return OnNoticeAsync != null ? OnNoticeAsync("warning", title, message, cancellationToken) : Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null, bool isDestructive = false, CancellationToken cancellationToken = default)
        {
            if (OnConfirm != null)
                return OnConfirm("confirm", title, message, "", primaryText, closeText, isDestructive, cancellationToken);
            return Task.FromResult(false);
        }

        public Task<string?> PromptAsync(string title, string message, string defaultValue = "", string? primaryText = null, string? closeText = null, int maxLength = 0, CancellationToken cancellationToken = default)
        {
            if (OnPrompt != null)
                return OnPrompt(title, message, defaultValue, primaryText, closeText, maxLength, cancellationToken);
            return Task.FromResult<string?>(null);
        }

        public Task<(string Password, bool Remember)?> PromptPasswordAsync(string title, string message, string rememberText, bool rememberByDefault, string? primaryText = null, string? closeText = null, string? errorText = null, CancellationToken cancellationToken = default)
        {
            if (OnPromptPassword != null)
                return OnPromptPassword(title, message, rememberText, rememberByDefault, primaryText, closeText, errorText, cancellationToken);
            return Task.FromResult<(string Password, bool Remember)?>(null);
        }

        public Task<(string Description, string Contact)?> PromptProblemReportAsync(string title, string message, string descriptionLabel, string descriptionPlaceholder, string contactLabel, string contactPlaceholder, string primaryText, string? closeText = null, int minDescriptionLength = 1, int maxDescriptionLength = 4000, int maxContactLength = 200, CancellationToken cancellationToken = default)
            => Task.FromResult<(string Description, string Contact)?>(null);

        public async Task<bool?> TryConfirmOnceAsync(string dedupeKey, string title, string message, string? primaryText = null, string? closeText = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(dedupeKey))
            {
                return await ConfirmAsync(title, message, primaryText, closeText, false, cancellationToken);
            }
            lock (_seen)
            {
                if (!_seen.Add(dedupeKey)) return null;
            }
            try
            {
                return await ConfirmAsync(title, message, primaryText, closeText, false, cancellationToken);
            }
            finally
            {
                lock (_seen)
                {
                    _seen.Remove(dedupeKey);
                }
            }
        }
    }

    public class LocalizationManager
    {
        private static readonly LocalizationManager _instance = new();
        private string _currentLangCode = "zh-Hans";

        private static readonly Dictionary<string, string> ZhHans = new(StringComparer.Ordinal)
        {
            // 网络与防火墙
            ["Network_Check_Running"] = "正在检查网络与防火墙…",
            ["Network_Check_Ok"] = "未发现问题：此网络上的音响可以正常连接到本机。",
            ["Network_Check_Unavailable"] = "无法运行网络检查：音频引擎未能启动或未及时响应，请重试。",
            ["Network_Check_TimedOut"] = "网络检查超时，请重试。",
            ["Network_FixFirewall_Title"] = "允许 AirStereo2 通过 Windows 防火墙？",
            ["Network_FixFirewall_NothingToFix"] = "网络检查未发现 Windows 防火墙拦截问题，可直接重试连接音响。",
            ["Network_FixFirewall_ScopeAll"] = "所有网络",
            ["Network_FixFirewall_ScopePrivatePublic"] = "专用和公用网络",
            ["Network_FixFirewall_ScopePrivateDomain"] = "专用和工作（域）网络",
            ["Network_FixFirewall_ScopePrivate"] = "专用网络",
            ["Network_FixFirewall_Message"] = "Windows 将请求管理员权限。AirStereo2 会添加一条允许{0}上的 AirPlay 音响连接到其音频引擎的规则，并移除旧的阻止规则，不会更改其他设置。",
            ["Network_FixFirewall_Confirm"] = "允许",
            ["Network_FixFirewall_Applied"] = "已更新 Windows 防火墙规则，现在音响可以连接到 AirStereo2，请重试连接。",
            ["Network_FixFirewall_StillBlocked"] = "已添加规则，但 Windows 防火墙仍阻止 AirStereo2。相关策略可能由组织管理。",
            ["Network_FixFirewall_FailedTitle"] = "未更改 Windows 防火墙",
            ["Network_FixFirewall_Unavailable"] = "未找到 AirStereo2 音频引擎，因此未添加防火墙规则。",
            ["Network_FixFirewall_Failed"] = "无法添加防火墙规则（错误码 {0}）。你可以在「Windows 安全中心 › 防火墙和网络保护 › 允许应用通过防火墙」中手动允许 AirStereo2。",
            ["Network_Hint_FirewallBlocksApp"] = "网络检查：Windows 防火墙正在此网络上阻止 AirStereo2，音响无法回传时钟同步数据。请点击“修复防火墙规则…”，或在「Windows 安全中心 › 防火墙和网络保护 › 允许应用通过防火墙」中放行。",
            ["Network_Hint_FirewallBlocksAllInbound"] = "网络检查：Windows 防火墙当前设置为阻止所有传入连接，音响无法回传时钟同步数据。请在「Windows 安全中心 › 防火墙和网络保护」中关闭“阻止所有传入连接”。",
            ["Network_Hint_FirewallNoAllowRule"] = "网络检查：缺少允许 AirStereo2 接收此网络上音响数据的 Windows 防火墙规则。请点击“修复防火墙规则…”放行。",
            ["Network_Hint_FirewallBlocksAppTcp"] = "网络检查：Windows 防火墙阻止了音响向 AirStereo2 的回连，音响端调节音量时同步可能较慢（不影响音频播放）。可使用“修复防火墙规则…”放行。",
            ["Network_Hint_FirewallNoTcpAllowRule"] = "网络检查：当前防火墙规则允许音频但未放行音响回连，音响端音量变化同步可能较慢。可使用“修复防火墙规则…”更新规则。",
            ["Network_Hint_PublicNetwork"] = "网络检查：Windows 将当前网络识别为“公用网络”，会限制局域网设备通信。若为家庭网络，请在「系统设置 › 网络和 Internet」中切换为“专用网络”。",
            ["Network_Hint_RouteViaVpn"] = "网络检查：发往此音响的流量正经过 VPN。请断开 VPN 或在其设置中允许访问局域网。",
            ["Network_Hint_RouteBypassesLan"] = "网络检查：本机发往此音响的流量未经过音响所在局域网网卡。请检查多网卡或 VPN 路由设置后重试。",
            ["Network_Hint_ReceiverOffSubnet"] = "网络检查：该音响与本机不在同一子网。AirPlay 需要双方处于同一局域网，请检查访客网络或路由器 AP 隔离设置。",
            ["Network_Hint_NoRoute"] = "网络检查：本机没有到达该音响的网络路由，请确认电脑与音响连接在同一网络。",
            ["Network_Hint_NoNetwork"] = "网络检查：本机当前没有活动的网络连接。",

            // 通用弹窗按钮与标题
            ["Dialog_OK"] = "确定",
            ["Dialog_Cancel"] = "取消",
            ["Dialog_Close"] = "关闭",
            ["Dialog_Yes"] = "是",
            ["Dialog_No"] = "否",
            ["Dialog_Info"] = "提示",
            ["Dialog_Error"] = "错误",

            // 设备连接与密码/配对
            ["Device_ConnectionFailedNoReason"] = "连接失败，后端未返回详细原因。",
            ["Device_AirPlay1SingleOnly"] = "AirPlay 1 设备目前仅支持单设备连接，请先断开其他音响。",
            ["Device_AirPlay2CannotJoinAirPlay1"] = "当前正处于 AirPlay 1 会话，请先断开再添加 AirPlay 2 音响。",
            ["Device_SelectedInstanceUnavailable"] = "所选 AirPlay 音响不再响应或网络地址已变更，请稍候或重新选择。",
            ["Device_NoReachableMember"] = "此 AirPlay 分组中没有可达设备，请检查 Wi-Fi 连接后重试。",
            ["Device_MixedTimingGroupUnsupported"] = "AirPlay 2 与传统 AirPlay 1/RAOP 设备不支持混合分组播放，请分别连接。",
            ["Device_AuthenticationRejected"] = "设备拒绝了身份验证。若音响设有密码或访问限制，请在 Apple「家庭」App 中检查访问权限。",
            ["Device_NoAirPlayAdvertisement"] = "该设备未广播 AirPlay 2 或 AirPlay 1/RAOP 音频支持。",
            ["Device_PasswordProtectedAirPlay1"] = "该 AirPlay 1 设备需要密码，暂不支持。",
            ["Device_BackendRecoveryExhausted"] = "音频引擎多次恢复后仍无响应，请重新连接或重启 AirStereo2。",
            ["Device_TakenOver"] = "其他设备正在此 HomePod 上播放，AirStereo2 已让出控制权。再次连接可抢回播放。",
            ["Device_DeviceBusy"] = "设备正忙于处理其他 AirPlay 或配对请求，请稍后重试。",
            ["Device_BackendExited"] = "音频引擎意外退出，AirStereo2 将尝试自动恢复。",
            ["Device_NetworkFeedbackTimeout"] = "连接中断：音响在网络上停止响应，请检查 Wi-Fi 后重试。",
            ["Device_MacOsAirPlayAccessDenied"] = "Mac 拒绝了 AirPlay 配对。请在 Mac 的「系统设置 → 通用 → 隔空投送与接力 → 隔空播放接收器」中允许同一网络设备访问并检查密码设置。",
            ["Device_PinFailed"] = "PIN 配对失败，请核对设备上显示的配对码后重试。",
            ["Device_PairingFailedDetail"] = "配对失败：{0}",
            ["Device_AuthFailedDetail"] = "身份验证失败：{0}",
            ["Device_RtspSetupFailedDetail"] = "会话建立失败：{0}",
            ["Device_CaptureFailedDetail"] = "音频采集失败：{0}",
            ["Device_TimeoutDetail"] = "连接超时：{0}",
            ["Device_NetworkErrorDetail"] = "网络错误：{0}",
            ["Device_ConnectionFailed"] = "连接失败：{0}",
            ["Device_BackendStartFailed"] = "无法启动音频引擎：{0}",
            ["Device_PasswordRequiredFailure"] = "该音响需要 AirPlay 密码（在「家庭」App 中设置）。",
            ["Device_ConnectTimeout"] = "连接超时（45 秒内音频引擎未返回结果）。",
            ["Device_PasswordTitle"] = "AirPlay 密码",
            ["Device_PasswordRemember"] = "在此电脑上记住密码",
            ["Device_PasswordConnect"] = "连接",
            ["Device_PasswordIncorrect"] = "音响未接受该密码，请重新输入。",
            ["Device_AuthErrorTitle"] = "身份验证失败",
            ["Device_DefaultOutputNone"] = "未选择",
            ["Device_DefaultOutputStereoPair"] = "立体声对",
            ["Device_DefaultOutputGroup"] = "分组 · {0} 只音响",
            ["Device_UnknownIp"] = "未知 IP",
            ["Device_AccessPasswordBadge"] = "受 AirPlay 密码保护（在「家庭」App 中设置）。",
            ["Device_AccessHomeOnlyBadge"] = "仅限共享此“家庭”的成员播放。如需允许此电脑，请在「家庭」App 中修改“扬声器与电视”访问权限。",
            ["Device_CalibratingSlow"] = "正在优化音频连接…",
            ["HomePod_UnpairedStereoTitle"] = "两只 HomePod 尚未组成立体声对",
            ["HomePod_UnpairedStereoMessage"] = "AirStereo2 将向两只 HomePod 发送相同音频，不会分离左右声道。如需原生左右立体声，请在 Apple「家庭」App 中创建立体声对。是否继续播放？",

            // 编码与均衡器与配对清理
            ["Codec_ChangeConfirmTitle"] = "切换音频编码",
            ["Codec_ChangeConfirmMessage"] = "切换音频编码会短暂停止播放并重新连接当前音响，是否继续？",
            ["Codec_MultiDevicePcmFallbackTitle"] = "PCM 将自动切换为 ALAC",
            ["Codec_MultiDevicePcmFallbackMessage"] = "多设备 AirPlay 2 播放时 PCM 不稳定，本次连接将统一使用 ALAC，是否继续？",
            ["Codec_IncompatibleTitle"] = "编码不受支持",
            ["Codec_IncompatibleMessage"] = "该设备不支持 {0}（{1}）。是否将全局编码切换为 {2} 并重试？",
            ["Codec_Reason_HomePodRequiresAlac"] = "HomePod 仅支持 ALAC 无损编码",
            ["Codec_CancelledMessage"] = "已取消连接：该设备不支持 {0}。",
            ["Codec_AirportPcmWarningTitle"] = "AirPort Express PCM 提示",
            ["Codec_AirportPcmWarningMessage"] = "你为旧款 AirPort Express 选择了 PCM。旧 Wi-Fi 下 ALAC 通常更稳定，但仍将按你的选择使用 PCM。",
            ["Pairings_NoneFound"] = "未找到已保存的配对记录。",
            ["Pairings_ClearTitle"] = "清除已保存的配对",
            ["Pairings_ClearConfirm"] = "这将断开当前 AirPlay 会话，并删除此电脑上保存的所有 PIN 配对记录，是否继续？",
            ["Pairings_ClearButton"] = "清除配对",
            ["Pairings_ClearFailed"] = "清除配对失败：{0}",
            ["Pairings_ClearSuccessTitle"] = "已完成",
            ["Pairings_ClearSuccess"] = "已清除所有保存的设备配对记录。",
            ["Eq_StatusCustom"] = "自定义",
            ["Eq_UserPresetPrefix"] = "自定义",
            ["Eq_SaveDialogTitle"] = "保存均衡器预设",
            ["Eq_SaveDialogMessage"] = "输入预设名称：",
            ["Eq_DeleteDialogTitle"] = "删除均衡器预设",
            ["Eq_ExportSuccess"] = "均衡器预设导出成功。",
            ["Eq_ExportFailed"] = "导出失败：{0}",
            ["Eq_ImportFailedGeneric"] = "导入失败。",
            ["Eq_ImportErrorEmpty"] = "导入文件为空。",
            ["Eq_ImportErrorInvalidJson"] = "无效的 JSON：{0}",
            ["Eq_ImportErrorNotPackage"] = "导入文件不包含有效的预设包。",
            ["Eq_ImportErrorFormat"] = "不支持的文件格式。{0}",
            ["Eq_ImportErrorSchema"] = "不支持的架构版本。{0}",
            ["Eq_ImportErrorBands"] = "均衡器频段布局不匹配。{0}",
            ["Eq_ImportFailed"] = "导入失败：{0}",
            ["Eq_ImportCount"] = "已导入 {0} 个自定义预设。",
            ["Eq_ImportNoneSkipped"] = "未导入新预设（内置预设已忽略或名称重复）。",
            ["Eq_ImportNone"] = "未导入自定义预设。",
            ["Eq_ImportSuccessDetail"] = "{0}\n已导入：{1}，重命名：{2}，跳过：{3}",
            ["Release_BackendRunning"] = "运行中",
            ["Release_BackendNotRunning"] = "未运行",
            ["Release_AppVersionFormat"] = "版本 {0}",
            ["Release_DiagnosticsExportedTitle"] = "故障诊断已导出",
            ["Release_DiagnosticsExportedMessage"] = "诊断报告已保存至：\n{0}",
            ["Release_DiagnosticsExportFailedTitle"] = "导出失败",
            ["Release_DiagnosticsExportFailedMessage"] = "无法保存诊断报告：{0}",
            ["Silence_TimeoutHoursFormat"] = "{0} 小时",
            ["Silence_TimeoutHoursMinutesFormat"] = "{0} 小时 {1} 分钟",
            ["Silence_TimeoutMinutesFormat"] = "{0} 分钟",
        };

        public static LocalizationManager Instance => _instance;
        public List<string> SupportedLanguages { get; } = new() { "en-US", "zh-Hans", "zh-Hant", "ja-JP", "ko-KR", "fr-FR", "es-ES", "ru-RU" };
        public int CurrentLanguageIndex => Math.Max(0, SupportedLanguages.IndexOf(_currentLangCode));
        public event Action? LanguageChanged;

        public void Initialize() { }
        public void ChangeLanguage(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return;
            if (string.Equals(_currentLangCode, langCode, StringComparison.OrdinalIgnoreCase)) return;
            _currentLangCode = langCode;
            LanguageChanged?.Invoke();
        }

        public string GetString(string key, string? fallback = null)
        {
            if (_currentLangCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                ZhHans.TryGetValue(key, out string? localized))
            {
                return localized;
            }
            return fallback ?? key;
        }

        public string Format(string key, string fallback, params object[] args)
        {
            string fmt = GetString(key, fallback);
            try { return args.Length > 0 ? string.Format(fmt, args) : fmt; }
            catch { return fmt; }
        }
    }
}
