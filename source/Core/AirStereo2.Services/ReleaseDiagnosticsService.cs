using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirStereo2.ViewModels;

namespace AirStereo2.Services;

public sealed class ReleaseDiagnosticsService
{
	public const int MaxUploadReportBytes = 4194304;

	private const int MaxUploadLogBytes = 1572864;

	private const int PreservedUploadHeaderBytes = 65536;

	private static readonly Regex Ipv4Pattern = new Regex("(?<![A-Za-z0-9])(?:\\d{1,3}\\.){3}\\d{1,3}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex VersionPrefixPattern = new Regex("(?i)(?:\\b(?:app(?:lication)?[ _-]?)?version)\\s*[:=#]?\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex Ipv6Pattern = new Regex("(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[A-Za-z0-9_.-]+)?(?![0-9A-Fa-f:])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex BracketedIpv6Pattern = new Regex("\\[[0-9A-Fa-f:%_.-]+\\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex MacAddressPattern = new Regex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex WindowsUserPathPattern = new Regex("(?i)(?<![A-Za-z0-9])([A-Z]:\\\\Users\\\\)[^\\\\\\r\\n]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex SecretLinePattern = new Regex("(?im)\\b(pin|passcode|password|private[_ -]?key|shared[_ -]?secret|access[_ -]?token)\\s*[:=]\\s*\\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex AuthorizationLinePattern = new Regex("(?im)\\bauthorization\\s*[:=]\\s*[^\\r\\n]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly string AppDataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

	private static readonly string LocalAppDataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

	public const string SessionSummaryFileName = "session-health-info.json";

	private static readonly TimeSpan LiveSummaryTimeout = TimeSpan.FromSeconds(2.0);

	public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

	public string AppDataPath => ResolveAppDataDirectory();

	public string BackendLogPath => Path.Combine(ResolveAppDataDirectory(), "service.log");

	public string BackendPath => ResolveBackendPath();

	public async Task<string> CollectSessionSummaryAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			string text = await AirPlayStreamer.Instance.RequestDiagnosticsSummaryAsync(LiveSummaryTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!string.IsNullOrWhiteSpace(text))
			{
				return FormatSessionSummary(text, "live");
			}
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
		}
		string path = Path.Combine(ResolveAppDataDirectory(), "session-health-info.json");
		try
		{
			if (File.Exists(path))
			{
				return FormatSessionSummary(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(continueOnCapturedContext: false), $"last persisted {File.GetLastWriteTimeUtc(path):O}");
			}
		}
		catch (Exception ex2) when (((ex2 is IOException || ex2 is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			return "(session summary unreadable: " + ex2.GetType().Name + ")";
		}
		return "(no session summary: backend not running and none persisted)";
	}

	internal static string FormatSessionSummary(string json, string source)
	{
		string value;
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			value = JsonSerializer.Serialize(jsonDocument.RootElement, new JsonSerializerOptions
			{
				WriteIndented = true
			});
		}
		catch (JsonException)
		{
			value = json.Trim();
		}
		return $"(source: {source}){Environment.NewLine}{value}";
	}

	private static void AppendBuildIdentity(StringBuilder sb, string appVersion)
	{
		BackendBuildInfo backendBuild = AirPlayStreamer.Instance.BackendBuild;
		if (backendBuild == null)
		{
			sb.AppendLine("Backend build: (not reported; backend not running or predates build stamping)");
			return;
		}
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
		handler.AppendLiteral("Backend build: ");
		handler.AppendFormatted(backendBuild);
		stringBuilder2.AppendLine(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
		handler.AppendLiteral("Backend build matches app version: ");
		handler.AppendFormatted(backendBuild.MatchesAppVersion(appVersion) ? "yes" : "NO");
		stringBuilder3.AppendLine(ref handler);
	}

	private static void AppendSessionSummary(StringBuilder sb, string? sessionSummary)
	{
		if (sessionSummary != null)
		{
			sb.AppendLine("Structured session summary:");
			sb.AppendLine(sessionSummary);
			sb.AppendLine();
		}
	}

	public string BuildReport(StoreLicenseSnapshot license, string? sessionSummary = null)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("AirStereo2 Diagnostics");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
		handler.AppendLiteral("Generated: ");
		handler.AppendFormatted(DateTimeOffset.Now, "O");
		stringBuilder3.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
		handler.AppendLiteral("App version: ");
		handler.AppendFormatted(AppVersion);
		stringBuilder4.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder2);
		handler.AppendLiteral("Windows version: ");
		handler.AppendFormatted(Environment.OSVersion.VersionString);
		stringBuilder5.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
		handler.AppendLiteral("Process architecture: ");
		handler.AppendFormatted(RuntimeInformation.ProcessArchitecture);
		stringBuilder6.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
		handler.AppendLiteral("App base directory: ");
		handler.AppendFormatted(AppDomain.CurrentDomain.BaseDirectory);
		stringBuilder7.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
		handler.AppendLiteral("App data directory: ");
		handler.AppendFormatted(AppDataLocation.RealPath(AppDataPath));
		stringBuilder8.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder2);
		handler.AppendLiteral("Packaged (MSIX): ");
		handler.AppendFormatted(AppDataLocation.IsPackaged() ? "yes" : "no");
		stringBuilder9.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder10 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
		handler.AppendLiteral("Backend path: ");
		handler.AppendFormatted(BackendPath);
		stringBuilder10.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder11 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
		handler.AppendLiteral("Backend exists: ");
		handler.AppendFormatted(File.Exists(BackendPath));
		stringBuilder11.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder12 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder2);
		handler.AppendLiteral("Backend connected: ");
		handler.AppendFormatted(SafeBackendConnected());
		stringBuilder12.AppendLine(ref handler);
		AppendBuildIdentity(stringBuilder, AppVersion);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder13 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2);
		handler.AppendLiteral("Backend log path: ");
		handler.AppendFormatted(AppDataLocation.RealPath(BackendLogPath));
		stringBuilder13.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder14 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
		handler.AppendLiteral("Backend log exists: ");
		handler.AppendFormatted(File.Exists(BackendLogPath));
		stringBuilder14.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder15 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
		handler.AppendLiteral("Store status: ");
		handler.AppendFormatted(license.StatusText);
		stringBuilder15.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder16 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder2);
		handler.AppendLiteral("Store entitlement: ");
		handler.AppendFormatted(license.EntitlementState);
		stringBuilder16.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder17 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("Store allows new connections: ");
		handler.AppendFormatted(license.AllowsNewConnections);
		stringBuilder17.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder18 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
		handler.AppendLiteral("Store context available: ");
		handler.AppendFormatted(license.IsStoreContextAvailable);
		stringBuilder18.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder19 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
		handler.AppendLiteral("Store license active: ");
		handler.AppendFormatted(license.IsActive);
		stringBuilder19.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder20 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
		handler.AppendLiteral("Store trial: ");
		handler.AppendFormatted(license.IsTrial);
		stringBuilder20.AppendLine(ref handler);
		if (license.ExpirationDate.HasValue)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder21 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2);
			handler.AppendLiteral("Store expiration: ");
			handler.AppendFormatted(license.ExpirationDate.Value, "O");
			stringBuilder21.AppendLine(ref handler);
		}
		if (!string.IsNullOrWhiteSpace(license.ErrorMessage))
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder22 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
			handler.AppendLiteral("Store error: ");
			handler.AppendFormatted(license.ErrorMessage);
			stringBuilder22.AppendLine(ref handler);
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("Configuration summary:");
		stringBuilder.AppendLine(JsonSerializer.Serialize(BuildConfigSummary(), new JsonSerializerOptions
		{
			WriteIndented = true,
			NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
		}));
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder23 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
		handler.AppendLiteral("Settings persistence: ");
		handler.AppendFormatted(SettingsManager.GetPersistenceDiagnostics());
		stringBuilder23.AppendLine(ref handler);
		stringBuilder.AppendLine();
		AppendSessionSummary(stringBuilder, sessionSummary);
		AppendDiscoverySnapshot(stringBuilder);
		return stringBuilder.ToString();
	}

	public string WriteReport(StoreLicenseSnapshot license)
	{
		Directory.CreateDirectory(AppDataDirectory);
		string text = Path.Combine(AppDataDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
		WriteReport(license, text);
		return text;
	}

	public string WriteReport(StoreLicenseSnapshot license, string path, string? sessionSummary = null)
	{
		string fullPath = Path.GetFullPath(path);
		string directoryName = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(BuildReport(license, sessionSummary));
		AppendBackendLog(stringBuilder);
		File.WriteAllText(fullPath, stringBuilder.ToString(), Encoding.UTF8);
		return fullPath;
	}

	public string BuildUploadReport(StoreLicenseSnapshot license, string? sessionSummary = null)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("AirStereo2 Diagnostics Upload");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder2);
		handler.AppendLiteral("Generated UTC: ");
		handler.AppendFormatted(DateTimeOffset.UtcNow, "O");
		stringBuilder3.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
		handler.AppendLiteral("App version: ");
		handler.AppendFormatted(AppVersion);
		stringBuilder4.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder2);
		handler.AppendLiteral("Windows version: ");
		handler.AppendFormatted(Environment.OSVersion.VersionString);
		stringBuilder5.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
		handler.AppendLiteral("Process architecture: ");
		handler.AppendFormatted(RuntimeInformation.ProcessArchitecture);
		stringBuilder6.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder2);
		handler.AppendLiteral("Backend packaged: ");
		handler.AppendFormatted(File.Exists(BackendPath));
		stringBuilder7.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder2);
		handler.AppendLiteral("Backend connected: ");
		handler.AppendFormatted(SafeBackendConnected());
		stringBuilder8.AppendLine(ref handler);
		AppendBuildIdentity(stringBuilder, AppVersion);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
		handler.AppendLiteral("Store status: ");
		handler.AppendFormatted(license.StatusText);
		stringBuilder9.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder10 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder2);
		handler.AppendLiteral("Store entitlement: ");
		handler.AppendFormatted(license.EntitlementState);
		stringBuilder10.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder11 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("Store allows new connections: ");
		handler.AppendFormatted(license.AllowsNewConnections);
		stringBuilder11.AppendLine(ref handler);
		if (!string.IsNullOrWhiteSpace(license.ErrorMessage))
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder12 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
			handler.AppendLiteral("Store error: ");
			handler.AppendFormatted(license.ErrorMessage);
			stringBuilder12.AppendLine(ref handler);
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("Configuration summary:");
		stringBuilder.AppendLine(JsonSerializer.Serialize(BuildConfigSummary(), new JsonSerializerOptions
		{
			WriteIndented = true,
			NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
		}));
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder13 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
		handler.AppendLiteral("Settings persistence: ");
		handler.AppendFormatted(SettingsManager.GetPersistenceDiagnostics());
		stringBuilder13.AppendLine(ref handler);
		stringBuilder.AppendLine();
		AppendSessionSummary(stringBuilder, sessionSummary);
		AppendDiscoverySnapshot(stringBuilder);
		AppendUploadLog(stringBuilder, BackendLogPath, "service.log (recent tail)");
		string path = Path.ChangeExtension(BackendLogPath, ".log.1");
		if (File.Exists(path))
		{
			AppendUploadLog(stringBuilder, path, "service.log.1 (recent tail)");
		}
		return LimitUtf8PreservingHeader(RedactForUpload(stringBuilder.ToString()), 4194304);
	}

	private static void AppendDiscoverySnapshot(StringBuilder sb)
	{
		sb.AppendLine("Discovery TXT snapshot (no pairing keys):");
		try
		{
			sb.AppendLine(CoreApp.GetRequiredService<HomeViewModel>().DiscoveryDiagnostics);
		}
		catch (Exception ex)
		{
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, sb);
			handler.AppendLiteral("(discovery snapshot unavailable: ");
			handler.AppendFormatted(ex.GetType().Name);
			handler.AppendLiteral(")");
			sb.AppendLine(ref handler);
		}
	}

	public static string RedactForUpload(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}
		string redacted = value;
		foreach (string item in new string[5]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			AppDataDirectory,
			LocalAppDataDirectory,
			AppDomain.CurrentDomain.BaseDirectory
		}.Where((string path) => !string.IsNullOrWhiteSpace(path)))
		{
			redacted = redacted.Replace(item, "<local-path>", StringComparison.OrdinalIgnoreCase);
		}
		string userName = Environment.UserName;
		if (userName.Length >= 3)
		{
			redacted = redacted.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
		}
		redacted = WindowsUserPathPattern.Replace(redacted, "$1<user>");
		redacted = AuthorizationLinePattern.Replace(redacted, "authorization=<redacted>");
		redacted = SecretLinePattern.Replace(redacted, (Match match) => match.Groups[1].Value + "=<redacted>");
		redacted = MacAddressPattern.Replace(redacted, "<mac-address>");
		redacted = BracketedIpv6Pattern.Replace(redacted, delegate(Match match)
		{
			string value2 = match.Value;
			return (!IsIpAddress(value2.Substring(1, value2.Length - 1 - 1))) ? match.Value : "[<ip-address>]";
		});
		redacted = Ipv6Pattern.Replace(redacted, (Match match) => (!IsIpAddress(match.Value)) ? match.Value : "<ip-address>");
		redacted = Ipv4Pattern.Replace(redacted, (Match match) => (!IsVersionLiteral(redacted, match) && IsIpAddress(match.Value)) ? "<ip-address>" : match.Value);
		return redacted;
	}

	private static bool IsVersionLiteral(string value, Match match)
	{
		int num = ((match.Index != 0) ? (value.LastIndexOfAny(new char[2] { '\r', '\n' }, match.Index - 1) + 1) : 0);
		Regex versionPrefixPattern = VersionPrefixPattern;
		int num2 = num;
		return versionPrefixPattern.IsMatch(value.Substring(num2, match.Index - num2));
	}

	private static string ResolveAppDataDirectory()
	{
		return new string[2] { AppDataDirectory, LocalAppDataDirectory }.Where((string path) => !string.IsNullOrWhiteSpace(path)).Distinct<string>(StringComparer.OrdinalIgnoreCase).FirstOrDefault((string path) => File.Exists(Path.Combine(path, "service.log"))) ?? AppDataDirectory;
	}

	private void AppendBackendLog(StringBuilder sb)
	{
		sb.AppendLine();
		sb.AppendLine("==================== service.log ====================");
		if (File.Exists(BackendLogPath))
		{
			AppendLogFile(sb, BackendLogPath);
		}
		else
		{
			sb.AppendLine("(service.log not found)");
		}
		string path = Path.ChangeExtension(BackendLogPath, ".log.1");
		if (File.Exists(path))
		{
			sb.AppendLine();
			sb.AppendLine("============== service.log.1 (previous) ==============");
			AppendLogFile(sb, path);
		}
	}

	private static void AppendLogFile(StringBuilder sb, string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
			char[] array = new char[65536];
			int charCount;
			while ((charCount = streamReader.Read(array, 0, array.Length)) > 0)
			{
				sb.Append(array, 0, charCount);
			}
		}
		catch (Exception ex)
		{
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(19, 2, sb);
			handler.AppendLiteral("(could not read ");
			handler.AppendFormatted(path);
			handler.AppendLiteral(": ");
			handler.AppendFormatted(ex.Message);
			handler.AppendLiteral(")");
			sb.AppendLine(ref handler);
		}
	}

	private static void AppendUploadLog(StringBuilder sb, string path, string title)
	{
		sb.AppendLine();
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder);
		handler.AppendLiteral("================ ");
		handler.AppendFormatted(title);
		handler.AppendLiteral(" ================");
		stringBuilder2.AppendLine(ref handler);
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long num = Math.Max(0L, fileStream.Length - 1572864);
			fileStream.Seek(num, SeekOrigin.Begin);
			using StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, num == 0);
			if (num > 0)
			{
				streamReader.ReadLine();
				sb.AppendLine("(older log content omitted)");
			}
			sb.Append(streamReader.ReadToEnd());
		}
		catch (Exception ex)
		{
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder);
			handler.AppendLiteral("(could not read log: ");
			handler.AppendFormatted(ex.Message);
			handler.AppendLiteral(")");
			stringBuilder3.AppendLine(ref handler);
		}
	}

	private static bool IsIpAddress(string value)
	{
		string text = value;
		int num = text.IndexOf('%');
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		IPAddress address;
		return IPAddress.TryParse(text, out address);
	}

	private static string LimitUtf8PreservingHeader(string value, int maxBytes)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		if (bytes.Length <= maxBytes)
		{
			return value;
		}
		byte[] bytes2 = Encoding.UTF8.GetBytes("\n\n... upload report truncated; newest log content follows ...\n\n");
		int num = Math.Min(65536, maxBytes / 4);
		while (num > 0 && num < bytes.Length && (bytes[num] & 0xC0) == 128)
		{
			num--;
		}
		int num2 = maxBytes - num - bytes2.Length;
		int i;
		for (i = bytes.Length - num2; i < bytes.Length && (bytes[i] & 0xC0) == 128; i++)
		{
		}
		return Encoding.UTF8.GetString(bytes, 0, num) + Encoding.UTF8.GetString(bytes2) + Encoding.UTF8.GetString(bytes, i, bytes.Length - i);
	}

	private static object BuildConfigSummary()
	{
		AppConfig appConfig = SettingsManager.ReadConfig(CloneConfig);
		SettingsManager.NormalizeAudioCodecConfig(appConfig);
		SettingsManager.NormalizeEqConfig(appConfig);
		return new
		{
			StreamingModeIndex = appConfig.StreamingModeIndex,
			CustomBufferSize = appConfig.CustomBufferSize,
			AudioCodec = appConfig.AudioCodec,
			CaptureSourceIndex = appConfig.CaptureSourceIndex,
			SilenceDetectionEnabled = appConfig.SilenceDetectionEnabled,
			SilenceTimeoutMinutes = appConfig.SilenceTimeoutMinutes,
			EqEnabled = appConfig.EqEnabled,
			EqPreampDb = appConfig.EqPreampDb,
			EqBandCount = (appConfig.EqBandGains?.Count ?? 0),
			DeviceMemoryCount = (appConfig.Devices?.Count ?? 0),
			DefaultOutputCount = (appConfig.DefaultOutputDevices?.Count ?? 0)
		};
	}

	private static AppConfig CloneConfig(AppConfig source)
	{
		try
		{
			return JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(source)) ?? new AppConfig();
		}
		catch
		{
			return new AppConfig();
		}
	}

	private static bool SafeBackendConnected()
	{
		try
		{
			return AirPlayStreamer.IsInstanceRunning;
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveBackendPath()
	{
		string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
		return Path.GetFullPath(Path.Combine(baseDirectory, "AirStereo2.Engine.exe"));
	}
}
