using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AirStereo2.Models;

namespace AirStereo2.Services;

public sealed class CalibrationReportService
{
	private static readonly Regex IpAddress = new Regex("(?<![A-Za-z0-9])(?:[0-9]{1,3}\\.){3}[0-9]{1,3}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex VersionPrefix = new Regex("(?i)(?:\\b(?:app(?:lication)?[ _-]?)?version|\\bv)\\s*[:=#]?\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex MacAddress = new Regex("(?i)(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex PotentialIpv6Address = new Regex("(?i)(?<![0-9a-z])\\[?(?<address>[0-9a-f:]{2,}(?:%[0-9a-z._-]+)?)\\]?(?![0-9a-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public void Export(string path, CalibrationReportData data, CalibrationReportOptions options)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		ArgumentNullException.ThrowIfNull(data, "data");
		ArgumentNullException.ThrowIfNull(options, "options");
		var value = new
		{
			schema = "airstereo2-latency-calibration-v1",
			privacy = new
			{
				generated_locally = true,
				uploaded_automatically = false,
				selected_fields = new
				{
					device_names = options.IncludeDeviceNames,
					network_addresses = options.IncludeNetworkAddresses,
					technical_metrics = options.IncludeTechnicalMetrics,
					backend_log = options.IncludeBackendLog
				}
			},
			app_version = data.AppVersion,
			started_at_utc = data.StartedAtUtc,
			completed_at_utc = data.CompletedAtUtc,
			topology = data.Topology,
			timing = data.Timing,
			codec = data.Codec,
			recommended_latency_ms = data.RecommendedLatencyMs,
			user_latency_ms = data.UserLatencyMs,
			devices = data.Devices.Select((CalibrationDeviceIdentity device, int index) => new
			{
				id = $"device-{index + 1}",
				name = (options.IncludeDeviceNames ? device.Name : null),
				model = device.Model,
				stable_id = (options.IncludeNetworkAddresses ? device.StableId : null),
				airplay2 = device.SupportsAirPlay2
			}),
			segments = (options.IncludeTechnicalMetrics ? data.Segments : null),
			backend_log = (options.IncludeBackendLog ? (from line in data.BackendLines.TakeLast(1500)
				select SanitizeLineForReport(line, options, data.Devices)).ToArray() : null)
		};
		string directoryName = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
		{
			WriteIndented = true,
			NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
		}));
	}

	internal static string SanitizeLine(string line, CalibrationReportOptions options)
	{
		if (!options.IncludeNetworkAddresses)
		{
			line = IpAddress.Replace(line, (Match match) => (!IsVersionLiteral(line, match) && IsIpAddress(match.Value)) ? "<ip-redacted>" : match.Value);
			line = PotentialIpv6Address.Replace(line, delegate(Match match)
			{
				string value = match.Groups["address"].Value;
				int num = value.IndexOf('%');
				IPAddress address;
				return (!IPAddress.TryParse((num >= 0) ? value.Substring(0, num) : value, out address) || address.AddressFamily != AddressFamily.InterNetworkV6) ? match.Value : "<ip-redacted>";
			});
			line = MacAddress.Replace(line, "<hardware-id-redacted>");
		}
		line = line.Replace('\r', ' ').Replace('\n', ' ');
		if (line.Length > 4000)
		{
			return line.Substring(0, 4000) + "…";
		}
		return line;
	}

	private static bool IsVersionLiteral(string value, Match match)
	{
		int num = ((match.Index != 0) ? (value.LastIndexOfAny(new char[2] { '\r', '\n' }, match.Index - 1) + 1) : 0);
		Regex versionPrefix = VersionPrefix;
		int num2 = num;
		return versionPrefix.IsMatch(value.Substring(num2, match.Index - num2));
	}

	private static bool IsIpAddress(string value)
	{
		IPAddress address;
		return IPAddress.TryParse(value, out address);
	}

	private static string SanitizeLineForReport(string line, CalibrationReportOptions options, IEnumerable<CalibrationDeviceIdentity> devices)
	{
		string text = SanitizeLine(line, options);
		foreach (CalibrationDeviceIdentity device in devices)
		{
			if (!options.IncludeDeviceNames && !string.IsNullOrWhiteSpace(device.Name))
			{
				text = text.Replace(device.Name, "<device-name-redacted>", StringComparison.OrdinalIgnoreCase);
			}
			if (!options.IncludeNetworkAddresses && !string.IsNullOrWhiteSpace(device.StableId))
			{
				text = text.Replace(device.StableId, "<device-id-redacted>", StringComparison.OrdinalIgnoreCase);
			}
		}
		return text;
	}
}
