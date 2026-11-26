using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirStereo2.Services;

public static class FirewallFix
{
	public const string RuleName = "AirStereo2 audio engine";

	public const string RuleGroup = "AirStereo2";

	private const string RuleDescription = "Lets AirPlay speakers on this network send timing, retransmit requests and volume changes to AirStereo2's audio engine.";

	internal static readonly string[] RuleProtocols = new string[2] { "UDP", "TCP" };

	internal const int ErrorCancelled = 1223;

	private static readonly TimeSpan HelperTimeout = TimeSpan.FromMinutes(2.0);

	private static readonly string[] FixableCodes = new string[4] { "firewall_blocks_app", "firewall_no_allow_rule", "firewall_blocks_app_tcp", "firewall_no_tcp_allow_rule" };

	private static readonly Dictionary<string, string> ProfileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["private"] = "Private",
		["public"] = "Public",
		["domain"] = "Domain"
	};

	private static readonly Dictionary<string, int> ProfileBits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		["domain"] = 1,
		["private"] = 2,
		["public"] = 4
	};

	public static bool IsFixable(string? code)
	{
		if (code != null)
		{
			return FixableCodes.Contains<string>(code, StringComparer.Ordinal);
		}
		return false;
	}

	public static FirewallFixPlan? PlanFor(NetworkCheckResult? result)
	{
		if (result == null || !IsAcceptableProgramPath(result.FirewallProgram))
		{
			return null;
		}
		List<NetworkFinding> list = result.Findings.Where((NetworkFinding finding) => IsFixable(finding.Code)).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		return new FirewallFixPlan(result.FirewallProgram, ProfilesFor(list));
	}

	public static IReadOnlyList<string> ProfilesFor(IEnumerable<NetworkFinding> findings)
	{
		List<string> list = new List<string> { "Private" };
		foreach (NetworkFinding finding in findings)
		{
			if (IsFixable(finding.Code) && finding.Profile != null && ProfileNames.TryGetValue(finding.Profile, out string value) && !list.Contains(value))
			{
				list.Add(value);
			}
		}
		return list;
	}

	public static bool IsAcceptableProgramPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path != path.Trim())
		{
			return false;
		}
		if (path.Length <= 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
		{
			return false;
		}
		if (path.Any((char c) => char.IsControl(c) || "\"<>|*?/".Contains(c)) || path.IndexOf(':', 2) >= 0)
		{
			return false;
		}
		string[] array = path.Substring(3).Split('\\');
		if (array.Any((string segment) => segment.Length == 0 || segment == "." || segment == ".."))
		{
			return false;
		}
		string text = array[^1];
		if (text.Equals("AirStereo2.Engine.exe", StringComparison.OrdinalIgnoreCase)
			|| text.StartsWith("airstereo2-engine", StringComparison.OrdinalIgnoreCase))
		{
			return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	/// <summary>
	/// Applies the Windows Firewall allow rules directly via the official Windows COM API
	/// (<c>HNetCfg.FwPolicy2</c> / <c>HNetCfg.FWRule</c>) without spawning PowerShell or cmd.
	/// Intended to be invoked from an elevated <c>AirStereo2.exe --fix-firewall</c> helper run.
	/// </summary>
	public static int RunElevatedApply(string program, string profilesCsv)
	{
		if (!OperatingSystem.IsWindows() || !IsAcceptableProgramPath(program) || !File.Exists(program))
		{
			return 2;
		}
		int profileMask = 0;
		foreach (string raw in (profilesCsv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (ProfileBits.TryGetValue(raw, out int bit))
			{
				profileMask |= bit;
			}
		}
		if (profileMask == 0)
		{
			profileMask = 2; // Private
		}

		try
		{
			string normalizedProgram = Path.GetFullPath(program);
			Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
			Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
			if (policyType == null || ruleType == null)
			{
				return 3;
			}
			dynamic? policy = Activator.CreateInstance(policyType);
			if (policy == null)
			{
				return 3;
			}

			// Collect names of existing Inbound rules targeting this exact engine binary (including Block rules)
			// or previous AirStereo2 audio engine rules so they can be replaced cleanly.
			List<string> toRemove = new List<string>();
			foreach (dynamic rule in policy.Rules)
			{
				try
				{
					if ((int)rule.Direction != 1) continue; // Inbound only
					string name = rule.Name as string ?? string.Empty;
					string? appName = rule.ApplicationName as string;
					bool matchesProgram = false;
					if (!string.IsNullOrWhiteSpace(appName))
					{
						string expanded;
						try { expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(appName.Trim())); }
						catch { expanded = appName.Trim(); }
						matchesProgram = string.Equals(expanded, normalizedProgram, StringComparison.OrdinalIgnoreCase);
					}
					bool matchesManagedName =
						string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(name, "AirStereo2 Audio Engine (UDP)", StringComparison.OrdinalIgnoreCase) ||
						string.Equals(name, "AirStereo2 Audio Engine (TCP)", StringComparison.OrdinalIgnoreCase);
					if ((matchesProgram || matchesManagedName) && !string.IsNullOrEmpty(name))
					{
						toRemove.Add(name);
					}
				}
				catch
				{
				}
			}

			foreach (string name in toRemove)
			{
				try
				{
					policy.Rules.Remove(name);
				}
				catch
				{
				}
			}

			(string DisplayName, int ProtocolNum)[] specs = new (string, int)[]
			{
				("AirStereo2 Audio Engine (UDP)", 17),
				("AirStereo2 Audio Engine (TCP)", 6)
			};
			foreach (var (displayName, protoNum) in specs)
			{
				dynamic? newRule = Activator.CreateInstance(ruleType);
				if (newRule == null) return 4;
				newRule.Name = displayName;
				newRule.Grouping = RuleGroup;
				newRule.Description = RuleDescription;
				newRule.ApplicationName = normalizedProgram;
				newRule.Direction = 1; // NET_FW_RULE_DIR_IN
				newRule.Action = 1;    // NET_FW_ACTION_ALLOW
				newRule.Protocol = protoNum;
				newRule.Profiles = profileMask;
				newRule.Enabled = true;
				policy.Rules.Add(newRule);
			}
			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			return 5;
		}
		catch (System.Runtime.InteropServices.COMException ex)
		{
			return ex.HResult != 0 ? ex.HResult : 6;
		}
		catch
		{
			return 1;
		}
	}

	internal static FirewallFixResult OutcomeOf(Exception? startError, int? exitCode)
	{
		if (startError is Win32Exception { NativeErrorCode: 1223 })
		{
			return new FirewallFixResult(FirewallFixOutcome.Cancelled);
		}
		if (startError != null || !exitCode.HasValue)
		{
			return new FirewallFixResult(FirewallFixOutcome.Failed, exitCode);
		}
		return new FirewallFixResult((exitCode != 0) ? FirewallFixOutcome.Failed : FirewallFixOutcome.Applied, exitCode);
	}

	public static async Task<FirewallFixResult> ApplyAsync(FirewallFixPlan plan, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!OperatingSystem.IsWindows() || !IsAcceptableProgramPath(plan.Program) || !File.Exists(plan.Program))
		{
			return new FirewallFixResult(FirewallFixOutcome.Unavailable);
		}
		string? selfExe = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(selfExe) || !File.Exists(selfExe))
		{
			return new FirewallFixResult(FirewallFixOutcome.Unavailable);
		}
		List<string> validProfiles = plan.Profiles
			.Where((string p) => ProfileNames.ContainsKey(p))
			.Select((string p) => ProfileNames[p])
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (validProfiles.Count == 0)
		{
			validProfiles.Add("Private");
		}
		string profilesArg = string.Join(",", validProfiles);
		ProcessStartInfo info = new ProcessStartInfo
		{
			FileName = selfExe,
			Arguments = $"--fix-firewall \"{plan.Program}\" \"{profilesArg}\"",
			UseShellExecute = true,
			Verb = "runas"
		};
		Process? process;
		try
		{
			process = await Task.Run(() => Process.Start(info), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Win32Exception startError)
		{
			return OutcomeOf(startError, null);
		}
		catch (OperationCanceledException)
		{
			return new FirewallFixResult(FirewallFixOutcome.Cancelled);
		}
		if (process == null)
		{
			return OutcomeOf(null, null);
		}
		using (process)
		{
			try
			{
				await process.WaitForExitAsync(cancellationToken).WaitAsync(HelperTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex2) when (ex2 is TimeoutException || ex2 is OperationCanceledException)
			{
				TryKill(process);
				return OutcomeOf(null, null);
			}
			return OutcomeOf(null, process.ExitCode);
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill();
			}
		}
		catch (Exception ex) when (((ex is Win32Exception || ex is InvalidOperationException || ex is NotSupportedException) ? 1 : 0) != 0)
		{
		}
	}
}
