using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

public static class NetworkCheckHint
{
	private static readonly Dictionary<string, (string Key, string English)> Hints = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
	{
		["firewall_blocks_app"] = ("Network_Hint_FirewallBlocksApp", "Network check: Windows Firewall is blocking AirStereo2 on this network, so the speaker cannot send timing data back. In Windows Security › Firewall & network protection › Allow an app through firewall, allow AirStereo2 for this network type."),
		["firewall_blocks_all_inbound"] = ("Network_Hint_FirewallBlocksAllInbound", "Network check: Windows Firewall is set to block all incoming connections, so the speaker cannot send timing data back. Turn off “Blocks all incoming connections” in Windows Security › Firewall & network protection."),
		["firewall_no_allow_rule"] = ("Network_Hint_FirewallNoAllowRule", "Network check: no Windows Firewall rule lets AirStereo2 receive data from speakers on this network. Allow AirStereo2 in Windows Security › Firewall & network protection › Allow an app through firewall."),
		["firewall_blocks_app_tcp"] = ("Network_Hint_FirewallBlocksAppTcp", "Network check: Windows Firewall blocks speakers' connections to AirStereo2 on this network, so a speaker cannot report volume changes made on it (AirStereo2 notices them only slowly). Audio is not affected. Use Fix firewall, or allow AirStereo2 in Windows Security › Firewall & network protection › Allow an app through firewall."),
		["firewall_no_tcp_allow_rule"] = ("Network_Hint_FirewallNoTcpAllowRule", "Network check: AirStereo2's firewall rule lets audio through but not speakers' connections, so a speaker cannot report volume changes made on it (AirStereo2 notices them only slowly). Use Fix firewall to update the rule."),
		["public_network"] = ("Network_Hint_PublicNetwork", "Network check: Windows treats this network as Public, which restricts traffic from local devices. If this is your home network, set its network profile to Private in Settings › Network & internet."),
		["route_via_vpn"] = ("Network_Hint_RouteViaVpn", "Network check: traffic to this speaker goes through a VPN. Disconnect the VPN or allow local network access in its settings."),
		["route_bypasses_lan"] = ("Network_Hint_RouteBypassesLan", "Network check: this PC sends traffic for this speaker through a different network adapter than the one on the speaker's network. Disconnect the other adapter or VPN and try again."),
		["receiver_off_subnet"] = ("Network_Hint_ReceiverOffSubnet", "Network check: this speaker is on a different subnet than this PC. AirPlay needs both on the same local network; check guest-network or router isolation settings."),
		["no_route"] = ("Network_Hint_NoRoute", "Network check: this PC has no network route to the speaker. Make sure the PC is connected to the same network."),
		["no_network"] = ("Network_Hint_NoNetwork", "Network check: this PC has no active network connection.")
	};

	private static readonly string[] NetworkSymptoms = new string[13]
	{
		"timeout", "timed out", "unreachable", "no reachable", "refused", "connection reset", "broken pipe", "forcibly closed", "network", "control eof",
		"control channel", "tcp probe failed", "no longer available"
	};

	private static readonly string[] CredentialReasons = new string[4] { "pin", "password", "authentication", "pairing" };

	public static bool IsNetworkSymptom(string? rawReason)
	{
		if (string.IsNullOrWhiteSpace(rawReason))
		{
			return false;
		}
		if (rawReason.Split(new char[6] { ' ', ':', ',', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).Any((string word) => CredentialReasons.Contains(word.ToLowerInvariant())))
		{
			return false;
		}
		return NetworkSymptoms.Any((string symptom) => rawReason.Contains(symptom, StringComparison.OrdinalIgnoreCase));
	}

	public static bool IsKnown(string code)
	{
		return Hints.ContainsKey(code);
	}

	public static bool CanExplainConnectionFailure(string code)
	{
		bool flag = ((code == "firewall_blocks_app_tcp" || code == "firewall_no_tcp_allow_rule") ? true : false);
		return !flag;
	}

	public static string? ResourceKey(string? code)
	{
		if (code == null || !Hints.TryGetValue(code, out (string, string) value))
		{
			return null;
		}
		return value.Item1;
	}

	public static string? Text(string? code, LocalizationManager? localization = null)
	{
		if (code == null || !Hints.TryGetValue(code, out (string, string) value))
		{
			return null;
		}
		return (localization ?? LocalizationManager.Instance).GetString(value.Item1, value.Item2);
	}

	public static string Append(string message, string? code, LocalizationManager? localization = null)
	{
		return AppendParagraph(message, Text(code, localization));
	}

	public static string AppendParagraph(string message, string? paragraph)
	{
		if (string.IsNullOrEmpty(paragraph))
		{
			return message;
		}
		if (!string.IsNullOrWhiteSpace(message))
		{
			return message.TrimEnd() + Environment.NewLine + Environment.NewLine + paragraph;
		}
		return paragraph;
	}
}
