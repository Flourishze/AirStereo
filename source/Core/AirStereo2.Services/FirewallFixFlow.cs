using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AirStereo2.Services;

public static class FirewallFixFlow
{
	internal enum FixPrecheck
	{
		Unavailable,
		TimedOut,
		NothingToFix,
		Fixable
	}

	private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10.0);

	private static int _running;

	public static IReadOnlyList<string> SettingsCheckTargets(IEnumerable<string?> speakerAddresses, int max = 8)
	{
		List<string> list = (from address in speakerAddresses
			where !string.IsNullOrWhiteSpace(address)
			select address.Trim() into address
			where NetworkCheckResult.ReceiverKey(new string[1] { address }) != null
			select address).Distinct<string>(StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, max)).ToList();
		if (list.Count <= 0)
		{
			return new string[1] { "127.0.0.1" };
		}
		return list;
	}

	public static async Task<NetworkCheckResult?> CheckAsync(IReadOnlyCollection<string> receivers, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			NetworkCheckResult? parsed = NetworkCheckResult.TryParse(await AirPlayStreamer.Instance.RequestNetworkCheckAsync(receivers, CheckTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: true));
			if (parsed != null && !string.Equals(parsed.Status, "timeout", StringComparison.OrdinalIgnoreCase))
			{
				string? program = parsed.FirewallProgram;
				if (!string.IsNullOrWhiteSpace(program))
				{
					IReadOnlyList<NetworkFinding> fwFindings = await Task.Run(() => InspectWindowsFirewall(program), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
					if (fwFindings.Count > 0)
					{
						parsed = parsed.WithAdditionalFindings(fwFindings);
					}
				}
			}
			return parsed;
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			return null;
		}
	}

	/// <summary>
	/// Inspects the real Windows Defender Firewall policy via COM (<c>HNetCfg.FwPolicy2</c>, no elevation required)
	/// for the active network profiles and returns matching <see cref="NetworkFinding"/> entries.
	/// </summary>
	public static IReadOnlyList<NetworkFinding> InspectWindowsFirewall(string programPath)
	{
		List<NetworkFinding> findings = new List<NetworkFinding>();
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(programPath))
		{
			return findings;
		}
		try
		{
			string normalizedTarget;
			try
			{
				normalizedTarget = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(programPath.Trim()));
			}
			catch
			{
				normalizedTarget = programPath.Trim();
			}

			Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
			if (policyType == null)
			{
				return findings;
			}
			dynamic? policy = Activator.CreateInstance(policyType);
			if (policy == null)
			{
				return findings;
			}

			int activeMask = (int)policy.CurrentProfileTypes;
			(int Bit, string Name)[] profiles = new (int, string)[]
			{
				(2, "private"),
				(4, "public"),
				(1, "domain")
			};

			// Collect matching enabled Inbound rules for this exact program once.
			List<(int Action, int Protocol, int Profiles, string LocalPorts)> matchingRules = new List<(int, int, int, string)>();
			foreach (dynamic rule in policy.Rules)
			{
				try
				{
					if (!(bool)rule.Enabled) continue;
					if ((int)rule.Direction != 1) continue; // 1 = NET_FW_RULE_DIR_IN
					string? appName = rule.ApplicationName as string;
					if (string.IsNullOrWhiteSpace(appName)) continue;
					string expandedApp;
					try
					{
						expandedApp = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(appName.Trim()));
					}
					catch
					{
						expandedApp = appName.Trim();
					}
					if (!string.Equals(expandedApp, normalizedTarget, StringComparison.OrdinalIgnoreCase)) continue;
					int action = (int)rule.Action;       // 0 = Block, 1 = Allow
					int protocol = (int)rule.Protocol;   // 6 = TCP, 17 = UDP, 256 = Any
					int ruleProfiles = (int)rule.Profiles;
					string localPorts = (rule.LocalPorts as string)?.Trim() ?? "*";
					matchingRules.Add((action, protocol, ruleProfiles, localPorts));
				}
				catch
				{
				}
			}

			foreach (var (bit, profileName) in profiles)
			{
				if ((activeMask & bit) == 0) continue;
				bool enabled = (bool)policy.FirewallEnabled(bit);
				if (!enabled) continue;

				bool blockAllInbound = (bool)policy.BlockAllInboundTraffic(bit);
				if (blockAllInbound)
				{
					findings.Add(new NetworkFinding("firewall_blocks_all_inbound", "error", null, profileName));
					continue;
				}

				bool defaultInboundBlock = (int)policy.DefaultInboundAction(bit) == 0; // 0 = Block, 1 = Allow
				bool udpBlocked = false;
				bool udpAllowed = false;
				bool tcpBlocked = false;
				bool tcpAllowed = false;

				foreach (var r in matchingRules)
				{
					if ((r.Profiles & bit) == 0) continue;
					bool coversAllPorts = string.IsNullOrEmpty(r.LocalPorts) || r.LocalPorts == "*";
					bool isUdp = r.Protocol == 17 || r.Protocol == 256;
					bool isTcp = r.Protocol == 6 || r.Protocol == 256;

					if (r.Action == 0) // Explicit Block always overrides Allow in Windows Firewall
					{
						if (isUdp && coversAllPorts) udpBlocked = true;
						if (isTcp && coversAllPorts) tcpBlocked = true;
					}
					else if (r.Action == 1) // Allow
					{
						if (isUdp && coversAllPorts) udpAllowed = true;
						if (isTcp && coversAllPorts) tcpAllowed = true;
					}
				}

				if (udpBlocked)
				{
					findings.Add(new NetworkFinding("firewall_blocks_app", "error", null, profileName));
				}
				else if (defaultInboundBlock && !udpAllowed)
				{
					findings.Add(new NetworkFinding("firewall_no_allow_rule", "error", null, profileName));
				}
				else if (tcpBlocked)
				{
					findings.Add(new NetworkFinding("firewall_blocks_app_tcp", "warning", null, profileName));
				}
				else if (defaultInboundBlock && !tcpAllowed)
				{
					findings.Add(new NetworkFinding("firewall_no_tcp_allow_rule", "warning", null, profileName));
				}
			}
		}
		catch
		{
		}
		return findings;
	}

	public static (string Key, string English) ScopeText(IReadOnlyCollection<string> profiles)
	{
		bool num = profiles.Contains<string>("Public", StringComparer.OrdinalIgnoreCase);
		bool flag = profiles.Contains<string>("Domain", StringComparer.OrdinalIgnoreCase);
		if (num)
		{
			if (flag)
			{
				return (Key: "Network_FixFirewall_ScopeAll", English: "all networks");
			}
			return (Key: "Network_FixFirewall_ScopePrivatePublic", English: "private and public networks");
		}
		if (flag)
		{
			return (Key: "Network_FixFirewall_ScopePrivateDomain", English: "private and work (domain) networks");
		}
		return (Key: "Network_FixFirewall_ScopePrivate", English: "private networks");
	}

	internal static (FixPrecheck Precheck, FirewallFixPlan? Plan) Precheck(NetworkCheckResult? before)
	{
		if (before == null)
		{
			return (Precheck: FixPrecheck.Unavailable, Plan: null);
		}
		if (before.Status == "timeout")
		{
			return (Precheck: FixPrecheck.TimedOut, Plan: null);
		}
		FirewallFixPlan firewallFixPlan = FirewallFix.PlanFor(before);
		if (!(firewallFixPlan == null))
		{
			return (Precheck: FixPrecheck.Fixable, Plan: firewallFixPlan);
		}
		return (Precheck: FixPrecheck.NothingToFix, Plan: null);
	}

	public static async Task<FirewallFixFlowResult> RunAsync(IReadOnlyCollection<string> receivers, IAppDialogService dialogs, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (Interlocked.Exchange(ref _running, 1) == 1)
		{
			return new FirewallFixFlowResult(null, null);
		}
		try
		{
			LocalizationManager loc = LocalizationManager.Instance;
			string title = loc.GetString("Network_FixFirewall_Title", "Allow AirStereo2 through Windows Firewall?");
			NetworkCheckResult before = await CheckAsync(receivers, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
			var (fixPrecheck, plan) = Precheck(before);
			switch (fixPrecheck)
			{
			case FixPrecheck.Unavailable:
				await dialogs.ShowWarningAsync(title, loc.GetString("Network_Check_Unavailable", "The network check could not run: the audio engine did not start or answer in time. Try again."), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
				return new FirewallFixFlowResult(null, null);
			case FixPrecheck.TimedOut:
				await dialogs.ShowWarningAsync(title, loc.GetString("Network_Check_TimedOut", "The network check took too long. Try again."), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
				return new FirewallFixFlowResult(null, before);
			case FixPrecheck.NothingToFix:
				await dialogs.ShowInfoAsync(title, loc.GetString("Network_FixFirewall_NothingToFix", "The network check no longer finds a Windows Firewall problem. Try connecting again."), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
				return new FirewallFixFlowResult(null, before);
			default:
			{
				if (plan == null)
				{
					return new FirewallFixFlowResult(null, before);
				}
				(string Key, string English) tuple2 = ScopeText(plan.Profiles);
				string item = tuple2.Key;
				string item2 = tuple2.English;
				string message = loc.Format("Network_FixFirewall_Message", "Windows will ask for administrator permission. AirStereo2 will add one rule that lets AirPlay speakers on {0} reach its audio engine, and remove older rules that block it. Nothing else changes.", loc.GetString(item, item2));
				if (!(await dialogs.ConfirmAsync(title, message, loc.GetString("Network_FixFirewall_Confirm", "Allow"), null, isDestructive: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: true)))
				{
					return new FirewallFixFlowResult(null, before);
				}
				FirewallFixResult result = await FirewallFix.ApplyAsync(plan, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
				switch (result.Outcome)
				{
				case FirewallFixOutcome.Applied:
				{
					NetworkCheckResult after = await CheckAsync(receivers, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
					return new FirewallFixFlowResult(result.Outcome, after);
				}
				case FirewallFixOutcome.Cancelled:
					return new FirewallFixFlowResult(result.Outcome, before);
				case FirewallFixOutcome.Unavailable:
					await dialogs.ShowErrorAsync(loc.GetString("Network_FixFirewall_FailedTitle", "Windows Firewall was not changed"), loc.GetString("Network_FixFirewall_Unavailable", "AirStereo2's audio engine could not be found, so no firewall rule was added."), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
					return new FirewallFixFlowResult(result.Outcome, before);
				default:
					await dialogs.ShowErrorAsync(loc.GetString("Network_FixFirewall_FailedTitle", "Windows Firewall was not changed"), loc.Format("Network_FixFirewall_Failed", "The firewall rule could not be added (code {0}). You can allow AirStereo2 in Windows Security › Firewall & network protection › Allow an app through firewall.", result.ExitCode?.ToString() ?? "-"), cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
					return new FirewallFixFlowResult(result.Outcome, before);
				}
			}
			}
		}
		finally
		{
			Interlocked.Exchange(ref _running, 0);
		}
	}

	public static bool StillBlocked(NetworkCheckResult? after, string? receiverIp = null)
	{
		if (after == null)
		{
			return false;
		}
		string targetKey = ((receiverIp == null) ? null : NetworkCheckResult.ReceiverKey(new string[1] { receiverIp }));
		return after.Findings.Any((NetworkFinding finding) => FirewallFix.IsFixable(finding.Code) && (finding.Receiver == null || receiverIp == null || (targetKey != null && NetworkCheckResult.ReceiverKey(new string[1] { finding.Receiver }) == targetKey)));
	}
}
