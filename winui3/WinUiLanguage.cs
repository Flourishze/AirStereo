using System.Globalization;
using System.IO;
using AirStereo.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AirStereo;

/// <summary>UI-only localization. Device identities, settings values and protocol logs are never translated.</summary>
public sealed class WinUiLanguage : DependencyObject
{
    private static readonly List<WeakReference<FrameworkElement>> targets = new();
    private static readonly string preferencePath = Path.Combine(PackageEnvironment.DataDirectory, "language.txt");
    internal static bool English { get; private set; } = ReadPreference();
    internal static event Action? Changed;

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(WinUiLanguage), new PropertyMetadata("", TextChanged));
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(WinUiLanguage), new PropertyMetadata("", TextChanged));
    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "Tracked", typeof(bool), typeof(WinUiLanguage), new PropertyMetadata(false));
    private static readonly DependencyProperty ArgumentsProperty = DependencyProperty.RegisterAttached(
        "Arguments", typeof(object), typeof(WinUiLanguage), new PropertyMetadata(null));

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetHint(DependencyObject target) => (string)target.GetValue(HintProperty);
    public static void SetHint(DependencyObject target, string value) => target.SetValue(HintProperty, value);

    private static void TextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (!(bool)element.GetValue(TrackedProperty))
        {
            element.SetValue(TrackedProperty, true);
            targets.RemoveAll(reference => !reference.TryGetTarget(out _));
            targets.Add(new WeakReference<FrameworkElement>(element));
        }
        Apply(element);
    }

    internal static void Message(FrameworkElement element, string key, params object[] arguments)
    {
        element.SetValue(ArgumentsProperty, arguments);
        SetText(element, key);
        Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        if (!element.DispatcherQueue.HasThreadAccess)
        {
            element.DispatcherQueue.TryEnqueue(() => Apply(element));
            return;
        }
        element.Language = English ? "en-US" : "zh-CN";
        string key = GetText(element);
        if (key.Length > 0)
        {
            string text = T(key);
            if (element.GetValue(ArgumentsProperty) is object[] arguments && arguments.Length > 0)
                text = string.Format(English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"), text, arguments);
            if (element is TextBlock block) block.Text = text;
            else if (element is BottomComboBox combo) combo.Header = text;
            else if (element is ContentControl control) control.Content = text;
        }
        string hint = GetHint(element);
        if (hint.Length > 0)
        {
            ToolTipService.SetToolTip(element, T(hint));
            AutomationProperties.SetName(element, T(hint));
        }
    }

    internal static void Set(bool english)
    {
        if (English == english) return;
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        string temporary = preferencePath + ".tmp";
        File.WriteAllText(temporary, english ? "en-US" : "zh-CN");
        File.Move(temporary, preferencePath, true);
        ApplyPreference(english);
    }

    // In-memory-only UI regression hook: never edits the user's preference file.
    internal static void ApplyPreference(bool english)
    {
        English = english;
        foreach (WeakReference<FrameworkElement> reference in targets.ToArray())
            if (reference.TryGetTarget(out FrameworkElement? element)) Apply(element);
        targets.RemoveAll(reference => !reference.TryGetTarget(out _));
        Changed?.Invoke();
    }

    private static bool ReadPreference()
    {
        try { return File.ReadAllText(preferencePath).Trim().Equals("en-US", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static string T(string key) => Translate(key, English);
    internal static string Translate(string key, bool english) => WinUiTextCatalog.Translate(key, english);
    internal static string Status(string text)
    {
        const string failed = "连接失败 · ";
        const string directory = "无法打开故障目录：";
        if (text.StartsWith(failed, StringComparison.Ordinal)) return T("连接失败") + " · " + T(text[failed.Length..]);
        if (text.StartsWith(directory, StringComparison.Ordinal)) return T(directory) + text[directory.Length..];
        return T(text);
    }

}

internal static class WinUiTextCatalog
{
    internal static string Translate(string key, bool english) => english && translations.TryGetValue(key, out string? text) ? text : key;

    private static readonly Dictionary<string, string> translations = new(StringComparer.Ordinal)
    {
        ["设置"] = "Settings",
        ["AirStereo · 设置"] = "AirStereo · Settings",
        ["常规"] = "General",
        ["均衡器"] = "Equalizer",
        ["诊断日志"] = "Diagnostic log",
        ["故障信息"] = "Fault information",
        ["播放与连接"] = "Playback & connection",
        ["语言 / Language"] = "Language / 语言",
        ["语言"] = "Language",
        ["语言切换立即生效。"] = "Language changes apply immediately.",
        ["无法保存语言设置：{0}"] = "Could not save the language preference: {0}",
        ["外观"] = "Appearance",
        ["即时生效，跟随 Windows 应用颜色模式。"] = "Applies immediately; follows the Windows app color mode.",
        ["跟随系统、浅色或深色模式会立即应用到托盘窗口和设置窗口。"] = "System, light and dark modes apply immediately to both windows.",
        ["跟随系统"] = "Use system setting",
        ["浅色"] = "Light",
        ["深色"] = "Dark",
        ["外观已应用并保存。"] = "Appearance applied and saved.",
        ["外观已应用但无法保存：{0}"] = "Appearance applied, but could not be saved: {0}",
        ["延迟缓冲"] = "Latency buffer",
        ["自定义延迟"] = "Custom latency",
        ["实时 · 120 ms"] = "Low latency · 120 ms",
        ["标准 · 200 ms"] = "Normal · 200 ms",
        ["缓冲 · 500 ms"] = "Buffered · 500 ms",
        ["稳定 · 1000 ms"] = "Stable · 1000 ms",
        ["自定义"] = "Custom",
        ["编码"] = "Codec",
        ["音频编码"] = "Audio encoding",
        ["ALAC·无损压缩"] = "ALAC · Lossless compression",
        ["PCM·未压缩"] = "PCM · Uncompressed",
        ["两者音质完全相同。ALAC 压缩后占用带宽约为 PCM 的一半；PCM 无需编码，占用带宽更高"] = "Both provide identical audio quality. ALAC compression uses roughly half the bandwidth of PCM; PCM requires no encoding but uses more bandwidth.",
        ["设置会保存到当前用户的数据目录。请先停止播放，再调整延迟、音频编码和采样率。"] = "Settings are saved for the current user. Stop playback before changing latency, audio encoding or sample rate.",
        ["采样率"] = "Sample rate",
        ["ALAC · 无损"] = "ALAC · Lossless",
        ["PCM · 兼容模式"] = "PCM · Compatibility",
        ["44.1 kHz · 音乐默认"] = "44.1 kHz · Music default",
        ["48 kHz · 视频/游戏"] = "48 kHz · Video/games",
        ["播放中断时自动重连"] = "Reconnect after playback interruption",
        ["最多重连次数"] = "Maximum retries",
        ["1–10 次"] = "1–10 attempts",
        ["系统行为"] = "System behavior",
        ["登录 Windows 后自动启动"] = "Start when I sign in to Windows",
        ["开机静默启动（仅显示托盘图标）"] = "Start silently (show tray icon only)",
        ["启动时自动连接上次选择的音响"] = "Connect to previous speakers on startup",
        ["串流时静音本机输出"] = "Mute local output while streaming",
        ["设置会保存到当前用户的数据目录。播放中的延迟和编码将在下一次连接时生效。"] = "Settings are saved for the current user. Latency and codec changes during playback apply on the next connection.",
        ["软件更新"] = "Software updates",
        ["检查更新"] = "Check for updates",
        ["商店更新"] = "Store updates",
        ["商店更新 · {0}"] = "Store updates · {0}",
        ["检查更新 · {0}"] = "Check for updates · {0}",
        ["查看更新与下载"] = "View release & download",
        ["打开项目说明"] = "Open project information",
        ["商店版由 Microsoft Store 管理更新。"] = "Microsoft Store manages updates for the Store edition.",
        ["更新来源：Flourishze/AirStereo 正式 Release。"] = "Update source: official Flourishze/AirStereo releases.",
        ["正在检查 GitHub 正式版本…"] = "Checking official GitHub releases…",
        ["仓库尚无可用的正式 Release"] = "No official release is available yet.",
        ["发现新版本 {0}，请查看发布说明并下载安装包。"] = "Version {0} is available. View the release notes and download the installer.",
        ["当前 {0}，远程 {1}，无需更新。"] = "Installed: {0}. Latest: {1}. No update needed.",
        ["检查更新失败，请检查网络或稍后重试（并非已是最新版）。"] = "Update check failed. Check your network or try again later; the latest version could not be verified.",
        ["无法打开 Microsoft Store，请在商店的下载页面检查更新"] = "Could not open Microsoft Store. Check for updates on its Downloads page.",
        ["无法打开浏览器，请稍后重试"] = "Could not open the browser. Please try again later.",
        ["播放平衡"] = "Balance",
        ["向左或向右调整播放声道平衡。单设备播放时此项不可用。"] = "Adjust the left/right balance. Unavailable for a single speaker.",
        ["启用均衡器"] = "Enable equalizer",
        ["均衡器预设"] = "Equalizer preset",
        ["十段峰值均衡器，调整即时生效；关闭后音频完全旁路。"] = "10-band peaking equalizer. Changes apply immediately; disabled EQ bypasses processing.",
        ["流行"] = "Pop", ["摇滚"] = "Rock", ["古典"] = "Classical", ["低音"] = "Bass",
        ["人声"] = "Vocal", ["爵士"] = "Jazz", ["电子"] = "Electronic", ["平坦"] = "Flat",
        ["居中"] = "Center",
        ["暂无最近提示。"] = "No recent notices.",
        ["暂无诊断日志。"] = "No diagnostic log entries.",
        ["暂无故障记录。\n\n故障文件目录：{0}"] = "No faults recorded.\n\nFault directory: {0}",
        ["打开故障目录"] = "Open fault directory",
        ["刷新故障列表"] = "Refresh fault list",
        ["电脑声音 · AirPlay 输出"] = "PC audio · AirPlay output",
        ["刷新音响列表"] = "Refresh speakers",
        ["收回到托盘"] = "Hide to tray",
        ["播放控制"] = "Playback",
        ["播放电脑声音"] = "Play PC audio",
        ["停止播放"] = "Stop playback",
        ["音量"] = "Volume",
        ["应用音量"] = "Apply volume",
        ["实时音量调整"] = "Real-time volume",
        ["在线"] = "Online", ["离线"] = "Offline",
        ["未知音响"] = "Unknown speaker",
        ["独立音响"] = "Independent speaker",
        ["原生立体声对 · {0} 只"] = "Native stereo pair · {0} speakers",
        ["{0} 只 · 软件立体声"] = "{0} speakers · Software stereo",
        ["配对"] = "Pair",
        ["正在扫描局域网中的 AirPlay 音响…"] = "Scanning the local network for AirPlay speakers…",
        ["网络不可用，请检查 IPv4 网络后刷新。"] = "Network unavailable. Check your IPv4 connection and refresh.",
        ["未发现音响，请确认电脑和音响在同一局域网。"] = "No speakers found. Ensure the PC and speakers are on the same local network.",
        ["正在准备…"] = "Preparing…", ["扫描中…"] = "Scanning…", ["扫描失败"] = "Scan failed",
        ["网络不可用"] = "Network unavailable", ["未发现音响"] = "No speakers found",
        ["已选择"] = "Selected", ["请选择音响"] = "Select speakers", ["请选择"] = "Select",
        ["连接中…"] = "Connecting…", ["缓冲中…"] = "Buffering…", ["播放中"] = "Playing",
        ["恢复连接…"] = "Reconnecting…", ["已停止"] = "Stopped", ["连接失败"] = "Connection failed",
        ["正在播放测试信号…"] = "Playing test signal…",
        ["播放期间不能调整设备选择，请先停止。"] = "Stop playback before changing speakers.",
        ["最多选择两只独立音响。"] = "Select up to two independent speakers.",
        ["请先勾选一只音响，或勾选两只独立音响组成 L/R 立体声。"] = "Select one speaker, or two independent speakers for L/R stereo.",
        ["无法打开故障目录："] = "Could not open the fault directory: ",
        ["启动项已在 Windows 中关闭，请到“设置 → 应用 → 启动”手动启用 AirStereo。"] = "Startup is disabled in Windows. Enable AirStereo in Settings > Apps > Startup.",
        ["Windows 策略禁止启用启动项。"] = "Windows policy prevents enabling startup.",
        ["Windows 策略不允许关闭此启动项。"] = "Windows policy prevents disabling this startup item.",
        ["连接或同步警告"] = "Connection or sync warning", ["播放中断"] = "Playback interrupted",
        ["音量设置失败"] = "Volume update failed", ["设置读取失败"] = "Settings read failed",
        ["设置保存失败"] = "Settings save failed",
        ["AirStereo · 立体声 AirPlay 发送器"] = "AirStereo · Stereo AirPlay sender",
        ["打开 AirStereo"] = "Open AirStereo", ["音频设置"] = "Audio settings", ["退出"] = "Exit"
    };
}
