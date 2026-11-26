using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AirStereo2.ViewModels;

namespace AirStereo2.Services;

public sealed class SilentStartupCoordinator
{
	internal const string ConnectFailedReason = "connect_failed";

	internal const string ConnectCanceledReason = "connect_canceled";

	public static readonly TimeSpan DefaultDiscoveryGrace = TimeSpan.FromSeconds(15.0);

	public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(45.0);

	private readonly Func<IReadOnlyList<DefaultOutputDeviceRecord>> _getDefaults;

	private readonly Func<IReadOnlyList<DeviceViewModel>> _getDevices;

	private readonly Func<DeviceViewModel, Task> _connectAsync;

	private readonly TimeSpan _discoveryGrace;

	private readonly TimeSpan _connectTimeout;

	private readonly TimeSpan _pollInterval;

	private readonly Func<DeviceViewModel, bool> _awaitsGroupMembers;

	public SilentStartupCoordinator(Func<IReadOnlyList<DefaultOutputDeviceRecord>> getDefaults, Func<IReadOnlyList<DeviceViewModel>> getDevices, Func<DeviceViewModel, Task> connectAsync, TimeSpan? discoveryGrace = null, TimeSpan? connectTimeout = null, TimeSpan? pollInterval = null, Func<DeviceViewModel, bool>? awaitsGroupMembers = null)
	{
		_getDefaults = getDefaults;
		_getDevices = getDevices;
		_connectAsync = connectAsync;
		_discoveryGrace = discoveryGrace ?? DefaultDiscoveryGrace;
		_connectTimeout = connectTimeout ?? DefaultConnectTimeout;
		_pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250.0);
		_awaitsGroupMembers = awaitsGroupMembers ?? ((Func<DeviceViewModel, bool>)((DeviceViewModel _) => false));
	}

	public async Task<SilentStartupReport> RunAsync(CancellationToken cancellationToken)
	{
		List<DefaultOutputDeviceRecord> list = _getDefaults()?.Where((DefaultOutputDeviceRecord d) => !string.IsNullOrWhiteSpace(d.StableId)).ToList() ?? new List<DefaultOutputDeviceRecord>();
		if (list.Count == 0)
		{
			return new SilentStartupReport
			{
				Kind = SilentStartupOutcomeKind.NoDefaults,
				Devices = Array.Empty<SilentStartupDeviceResult>()
			};
		}
		using (SilentStartupContext.Enter())
		{
			Dictionary<string, DefaultOutputDeviceRecord> pending = (from g in list.GroupBy<DefaultOutputDeviceRecord, string>((DefaultOutputDeviceRecord d) => d.StableId, StringComparer.OrdinalIgnoreCase)
				select g.First()).ToDictionary<DefaultOutputDeviceRecord, string, DefaultOutputDeviceRecord>((DefaultOutputDeviceRecord d) => d.StableId, (DefaultOutputDeviceRecord d) => d, StringComparer.OrdinalIgnoreCase);
			Dictionary<string, SilentStartupDeviceResult> results = pending.Values.ToDictionary<DefaultOutputDeviceRecord, string, SilentStartupDeviceResult>((DefaultOutputDeviceRecord d) => d.StableId, (DefaultOutputDeviceRecord d) => new SilentStartupDeviceResult
			{
				StableId = d.StableId,
				DisplayName = (string.IsNullOrWhiteSpace(d.DisplayName) ? d.StableId : d.DisplayName),
				Connected = false,
				Discovered = false,
				ActionRequired = false
			}, StringComparer.OrdinalIgnoreCase);
			HashSet<string> started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, (string? Name, string? Address, string? Model)> hinted = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, Task<string?>> connectionAttempts = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
			DateTime deadline = DateTime.UtcNow + _discoveryGrace;
			while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
			{
				TryKickConnections(pending, results, started, hinted, connectionAttempts, finalPass: false, cancellationToken);
				if (results.Values.All((SilentStartupDeviceResult r) => r.Connected))
				{
					break;
				}
				await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			cancellationToken.ThrowIfCancellationRequested();
			TryKickConnections(pending, results, started, hinted, connectionAttempts, finalPass: true, cancellationToken);
			DateTime connectDeadline = DateTime.UtcNow + _connectTimeout;
			while (DateTime.UtcNow < connectDeadline && !cancellationToken.IsCancellationRequested)
			{
				RefreshConnectionAttempts(connectionAttempts, results);
				RefreshResults(results);
				if (results.Values.All(IsTerminalResult))
				{
					bool num = connectionAttempts.Values.Any((Task<string> task) => !task.IsCompleted);
					bool flag = _getDevices().Any((DeviceViewModel d) => !string.IsNullOrWhiteSpace(d.StableId) && d.IsConnecting && results.ContainsKey(d.StableId) && !results[d.StableId].Connected);
					if (!num && !flag)
					{
						break;
					}
				}
				await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			cancellationToken.ThrowIfCancellationRequested();
			RefreshConnectionAttempts(connectionAttempts, results);
			RefreshResults(results);
			foreach (KeyValuePair<string, SilentStartupDeviceResult> item in results.ToList())
			{
				if (!item.Value.Discovered && !item.Value.Connected)
				{
					results[item.Key] = item.Value with
					{
						Detail = "not_discovered"
					};
				}
			}
			List<SilentStartupDeviceResult> list2 = results.Values.ToList();
			bool flag2 = list2.All((SilentStartupDeviceResult r) => r.Connected);
			return new SilentStartupReport
			{
				Kind = (flag2 ? SilentStartupOutcomeKind.AllSucceeded : SilentStartupOutcomeKind.PartialOrFailed),
				Devices = list2
			};
		}
	}

	public async Task<SilentStartupReport> RetryUnresolvedAsync(SilentStartupReport previous, CancellationToken cancellationToken)
	{
		HashSet<string> unresolvedIds = previous.Unresolved.Select((SilentStartupDeviceResult u) => u.StableId).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (unresolvedIds.Count == 0)
		{
			return previous;
		}
		List<DefaultOutputDeviceRecord> subset = (from d in _getDefaults()
			where unresolvedIds.Contains(d.StableId)
			select d).ToList();
		SilentStartupReport silentStartupReport = await new SilentStartupCoordinator(() => subset, _getDevices, _connectAsync, _discoveryGrace, _connectTimeout, _pollInterval, _awaitsGroupMembers).RunAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		Dictionary<string, SilentStartupDeviceResult> dictionary = new Dictionary<string, SilentStartupDeviceResult>(StringComparer.OrdinalIgnoreCase);
		foreach (SilentStartupDeviceResult device in previous.Devices)
		{
			if (!string.IsNullOrWhiteSpace(device.StableId))
			{
				dictionary[device.StableId] = device;
			}
		}
		foreach (SilentStartupDeviceResult device2 in silentStartupReport.Devices)
		{
			dictionary[device2.StableId] = device2;
		}
		List<SilentStartupDeviceResult> list = dictionary.Values.ToList();
		return new SilentStartupReport
		{
			Kind = (list.All((SilentStartupDeviceResult d) => d.Connected) ? SilentStartupOutcomeKind.AllSucceeded : SilentStartupOutcomeKind.PartialOrFailed),
			Devices = list
		};
	}

	private void TryKickConnections(Dictionary<string, DefaultOutputDeviceRecord> pending, Dictionary<string, SilentStartupDeviceResult> results, HashSet<string> started, Dictionary<string, (string? Name, string? Address, string? Model)> hinted, Dictionary<string, Task<string?>> connectionAttempts, bool finalPass, CancellationToken cancellationToken)
	{
		foreach (DeviceViewModel item in _getDevices())
		{
			if (string.IsNullOrWhiteSpace(item.StableId) || !pending.ContainsKey(item.StableId))
			{
				continue;
			}
			results[item.StableId] = results[item.StableId]with
			{
				Discovered = true,
				DisplayName = (string.IsNullOrWhiteSpace(item.Name) ? results[item.StableId].DisplayName : item.Name)
			};
			(string, string, string) tuple = (item.Name, item.IPAddress, item.Model);
			if (hinted.TryGetValue(item.StableId, out (string, string, string) value))
			{
				(string, string, string) tuple2 = value;
				(string, string, string) tuple3 = tuple;
				if (!(tuple2.Item1 != tuple3.Item1) && !(tuple2.Item2 != tuple3.Item2) && !(tuple2.Item3 != tuple3.Item3))
				{
					goto IL_0128;
				}
			}
			hinted[item.StableId] = tuple;
			SettingsManager.TouchDefaultOutputHint(item.StableId, item.Name, item.IPAddress, item.Model);
			goto IL_0128;
			IL_0128:
			if (item.IsConnected)
			{
				results[item.StableId] = results[item.StableId]with
				{
					Connected = true,
					ActionRequired = false,
					Detail = null
				};
			}
			else if (item.IsPinRequested)
			{
				results[item.StableId] = results[item.StableId]with
				{
					ActionRequired = true,
					Detail = "pin_required"
				};
			}
			else if (!started.Contains(item.StableId) && !item.IsConnecting && (finalPass || !_awaitsGroupMembers(item)))
			{
				started.Add(item.StableId);
				connectionAttempts[item.StableId] = ObserveConnectionAsync(item, cancellationToken);
			}
		}
	}

	private async Task<string?> ObserveConnectionAsync(DeviceViewModel device, CancellationToken cancellationToken)
	{
		try
		{
			await _connectAsync(device).ConfigureAwait(continueOnCapturedContext: false);
			return null;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (OperationCanceledException)
		{
			return "connect_canceled";
		}
		catch (Exception ex3)
		{
			string text = ex3.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
			if (text.Length > 240)
			{
				text = text.Substring(0, 240);
			}
			return string.IsNullOrWhiteSpace(text) ? "connect_failed" : ("connect_failed: " + text);
		}
	}

	private static void RefreshConnectionAttempts(Dictionary<string, Task<string?>> connectionAttempts, Dictionary<string, SilentStartupDeviceResult> results)
	{
		foreach (var (key, task2) in connectionAttempts)
		{
			if (task2.IsCompletedSuccessfully)
			{
				string result = task2.Result;
				if (result != null && results.TryGetValue(key, out SilentStartupDeviceResult value) && !value.Connected)
				{
					results[key] = value with
					{
						Discovered = true,
						ActionRequired = false,
						Detail = result
					};
				}
			}
		}
	}

	private static bool IsTerminalResult(SilentStartupDeviceResult result)
	{
		if (!result.Connected && !result.ActionRequired && result.Discovered)
		{
			return !string.IsNullOrWhiteSpace(result.Detail);
		}
		return true;
	}

	private void RefreshResults(Dictionary<string, SilentStartupDeviceResult> results)
	{
		foreach (DeviceViewModel item in _getDevices())
		{
			if (!string.IsNullOrWhiteSpace(item.StableId) && results.ContainsKey(item.StableId))
			{
				if (item.IsConnected)
				{
					results[item.StableId] = results[item.StableId]with
					{
						Connected = true,
						Discovered = true,
						ActionRequired = false,
						Detail = null
					};
				}
				else if (item.IsPinRequested)
				{
					results[item.StableId] = results[item.StableId]with
					{
						Discovered = true,
						ActionRequired = true,
						Detail = "pin_required"
					};
				}
				else if (item.IsFailed)
				{
					results[item.StableId] = results[item.StableId]with
					{
						Discovered = true,
						ActionRequired = false,
						Detail = (string.IsNullOrWhiteSpace(item.FailureMessage) ? "failed" : item.FailureMessage)
					};
				}
				else if (!string.IsNullOrWhiteSpace(item.IPAddress))
				{
					results[item.StableId] = results[item.StableId]with
					{
						Discovered = true
					};
				}
			}
		}
	}
}
