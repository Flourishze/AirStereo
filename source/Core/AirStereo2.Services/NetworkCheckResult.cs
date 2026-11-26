using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace AirStereo2.Services;

public sealed class NetworkCheckResult
{
	private static readonly string[] SeverityOrder = new string[2] { "error", "warning" };

	public string Status { get; }

	public IReadOnlyList<NetworkFinding> Findings { get; }

	public IReadOnlyList<string> Receivers { get; }

	public string? FirewallProgram { get; }

	private NetworkCheckResult(string status, IReadOnlyList<NetworkFinding> findings, IReadOnlyList<string> receivers, string? firewallProgram)
	{
		Status = status;
		Findings = findings;
		Receivers = receivers;
		FirewallProgram = firewallProgram;
	}

	internal NetworkCheckResult WithAdditionalFindings(IEnumerable<NetworkFinding> extra)
	{
		List<NetworkFinding> merged = new List<NetworkFinding>(Findings);
		foreach (NetworkFinding finding in extra)
		{
			if (!merged.Any((NetworkFinding existing) =>
				string.Equals(existing.Code, finding.Code, StringComparison.Ordinal) &&
				string.Equals(existing.Profile, finding.Profile, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(existing.Receiver, finding.Receiver, StringComparison.OrdinalIgnoreCase)))
			{
				merged.Add(finding);
			}
		}
		return new NetworkCheckResult(Status, merged, Receivers, FirewallProgram);
	}

	public static NetworkCheckResult? TryParse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("status", out var value) || value.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			List<NetworkFinding> list = new List<NetworkFinding>();
			if (rootElement.TryGetProperty("findings", out var value2) && value2.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in value2.EnumerateArray())
				{
					string text = ReadString(item, "code");
					string text2 = ReadString(item, "severity");
					if (text != null && text2 != null)
					{
						list.Add(new NetworkFinding(text, text2, ReadString(item, "receiver"), ReadString(item, "profile")));
					}
				}
			}
			List<string> list2 = new List<string>();
			if (rootElement.TryGetProperty("receivers", out var value3) && value3.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item2 in value3.EnumerateArray())
				{
					string text3 = ReadString(item2, "ip");
					if (text3 != null)
					{
						list2.Add(text3);
					}
				}
			}
			JsonElement value4;
			string firewallProgram = (rootElement.TryGetProperty("firewall", out value4) ? ReadString(value4, "program") : null);
			return new NetworkCheckResult(value.GetString() ?? string.Empty, list, list2, firewallProgram);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static string? ReceiverKey(IEnumerable<string> addresses)
	{
		List<string> list = new List<string>();
		foreach (string address2 in addresses)
		{
			if (!IPAddress.TryParse(address2.Trim(), out IPAddress address))
			{
				return null;
			}
			list.Add(address.ToString());
		}
		return string.Join(",", list.Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string x) => x, StringComparer.OrdinalIgnoreCase));
	}

	public NetworkFinding? MostRelevantFor(string receiverIp, bool includeWarnings = true)
	{
		string key = ReceiverKey(new string[1] { receiverIp });
		foreach (string severity in SeverityOrder.Take((!includeWarnings) ? 1 : SeverityOrder.Length))
		{
			NetworkFinding networkFinding = Findings.FirstOrDefault((NetworkFinding finding) => string.Equals(finding.Severity, severity, StringComparison.Ordinal) && NetworkCheckHint.IsKnown(finding.Code) && NetworkCheckHint.CanExplainConnectionFailure(finding.Code) && (finding.Receiver == null || ReceiverKey(new string[1] { finding.Receiver }) == key));
			if (networkFinding != null)
			{
				return networkFinding;
			}
		}
		return null;
	}

	private static string? ReadString(JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}
}
