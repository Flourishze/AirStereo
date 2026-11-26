using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AirStereo2.Services;

public sealed class LocalOutputMuteService : IDisposable, IMMNotificationClient
{
	private static readonly string LeasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "local_mute.status");

	private readonly LocalOutputMuteState _state = new LocalOutputMuteState();

	private MMDeviceEnumerator? _enumerator;

	private LocalOutputMuteLease? _lease;

	private bool _started;

	private bool _disposed;

	public static bool WasCreated { get; private set; }

	public LocalOutputMuteService()
	{
		WasCreated = true;
	}

	public void Start()
	{
		if (!_started && !_disposed)
		{
			_started = true;
			RecoverAbandonedLease();
			AirPlayStreamer.Instance.LogReceived += OnBackendLogReceived;
			SetEnabled(SettingsManager.ReadConfig((AppConfig config) => config.MuteLocalOutputWhileStreaming));
		}
	}

	public void SetEnabled(bool enabled)
	{
		Dispatch(delegate
		{
			if (!_disposed)
			{
				_state.SetEnabled(enabled);
				if (enabled)
				{
					EnsureEndpointNotifications();
				}
				ReconcileMuteState();
				if (!enabled)
				{
					ReleaseEndpointNotifications();
				}
			}
		});
	}

	private void OnBackendLogReceived(string line)
	{
		Dispatch(delegate
		{
			if (!_disposed)
			{
				_state.ApplyBackendLine(line);
				ReconcileMuteState();
			}
		});
	}

	private void ReconcileMuteState()
	{
		if (_state.ShouldMute)
		{
			MuteCurrentDefaultEndpoint();
		}
		else
		{
			RestoreLease();
		}
	}

	private void MuteCurrentDefaultEndpoint()
	{
		try
		{
			EnsureEndpointNotifications();
			if (_enumerator == null)
			{
				return;
			}
			MMDevice defaultAudioEndpoint = _enumerator.GetDefaultAudioEndpoint((DataFlow)0, (Role)0);
			try
			{
				if (_lease?.DeviceId == defaultAudioEndpoint.ID)
				{
					return;
				}
				RestoreLease();
				bool mute = defaultAudioEndpoint.AudioEndpointVolume.Mute;
				LocalOutputMuteLease lease = CreateLease(defaultAudioEndpoint.ID, mute);
				if (!TryPersistLease(lease))
				{
					return;
				}
				try
				{
					defaultAudioEndpoint.AudioEndpointVolume.Mute = true;
					_lease = lease;
				}
				catch
				{
					DeletePersistedLease();
					throw;
				}
			}
			finally
			{
				((IDisposable)defaultAudioEndpoint)?.Dispose();
			}
		}
		catch (Exception)
		{
			RestoreLease();
		}
	}

	private void RestoreLease()
	{
		LocalOutputMuteLease lease = _lease;
		if (lease == null || _enumerator == null)
		{
			return;
		}
		try
		{
			MMDevice device = _enumerator.GetDevice(lease.DeviceId);
			try
			{
				if (device.AudioEndpointVolume.Mute)
				{
					device.AudioEndpointVolume.Mute = lease.WasMuted;
				}
				_lease = null;
				DeletePersistedLease();
			}
			finally
			{
				((IDisposable)device)?.Dispose();
			}
		}
		catch (Exception)
		{
		}
	}

	private static bool TryPersistLease(LocalOutputMuteLease lease)
	{
		string text = LeasePath + ".tmp";
		try
		{
			if (File.Exists(LeasePath))
			{
				LocalOutputMuteLease localOutputMuteLease = JsonSerializer.Deserialize<LocalOutputMuteLease>(File.ReadAllText(LeasePath));
				if (localOutputMuteLease != null && IsLeaseOwnerActive(localOutputMuteLease) && (localOutputMuteLease.OwnerProcessId != lease.OwnerProcessId || localOutputMuteLease.OwnerStartTimeUtcTicks != lease.OwnerStartTimeUtcTicks))
				{
					return false;
				}
			}
			string directoryName = Path.GetDirectoryName(LeasePath);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(text, JsonSerializer.Serialize(lease));
			File.Move(text, LeasePath, overwrite: true);
			return true;
		}
		catch (Exception)
		{
			try
			{
				File.Delete(text);
			}
			catch
			{
			}
			return false;
		}
	}

	private static LocalOutputMuteLease CreateLease(string deviceId, bool wasMuted)
	{
		using Process process = Process.GetCurrentProcess();
		long ownerStartTimeUtcTicks;
		try
		{
			ownerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
		}
		catch
		{
			ownerStartTimeUtcTicks = 0L;
		}
		return new LocalOutputMuteLease(deviceId, wasMuted, process.Id, ownerStartTimeUtcTicks);
	}

	private static void DeletePersistedLease()
	{
		try
		{
			File.Delete(LeasePath);
		}
		catch (Exception)
		{
		}
	}

	private static void RecoverAbandonedLease()
	{
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Expected O, but got Unknown
		if (!File.Exists(LeasePath))
		{
			return;
		}
		LocalOutputMuteLease localOutputMuteLease;
		try
		{
			localOutputMuteLease = JsonSerializer.Deserialize<LocalOutputMuteLease>(File.ReadAllText(LeasePath));
		}
		catch (Exception)
		{
			DeletePersistedLease();
			return;
		}
		if (localOutputMuteLease == null || string.IsNullOrWhiteSpace(localOutputMuteLease.DeviceId))
		{
			DeletePersistedLease();
		}
		else
		{
			if (IsLeaseOwnerActive(localOutputMuteLease))
			{
				return;
			}
			if (!localOutputMuteLease.WasMuted)
			{
				try
				{
					MMDeviceEnumerator val = new MMDeviceEnumerator();
					try
					{
						MMDevice device = val.GetDevice(localOutputMuteLease.DeviceId);
						try
						{
							bool mute = device.AudioEndpointVolume.Mute;
							if (ShouldRestoreAbandonedLease(localOutputMuteLease.WasMuted, mute))
							{
								device.AudioEndpointVolume.Mute = false;
							}
							DeletePersistedLease();
							return;
						}
						finally
						{
							((IDisposable)device)?.Dispose();
						}
					}
					finally
					{
						((IDisposable)val)?.Dispose();
					}
				}
				catch (Exception)
				{
					return;
				}
			}
			DeletePersistedLease();
		}
	}

	internal static bool ShouldRestoreAbandonedLease(bool wasMuted, bool currentlyMuted)
	{
		return !wasMuted && currentlyMuted;
	}

	private static bool IsLeaseOwnerActive(LocalOutputMuteLease lease)
	{
		if (lease.OwnerProcessId <= 0 || lease.OwnerStartTimeUtcTicks <= 0)
		{
			return false;
		}
		try
		{
			using Process process = Process.GetProcessById(lease.OwnerProcessId);
			return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.OwnerStartTimeUtcTicks;
		}
		catch
		{
			return false;
		}
	}

	private void EnsureEndpointNotifications()
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Expected O, but got Unknown
		if (_enumerator != null || _disposed)
		{
			return;
		}
		try
		{
			_enumerator = new MMDeviceEnumerator();
			_enumerator.RegisterEndpointNotificationCallback((IMMNotificationClient)(object)this);
		}
		catch (Exception)
		{
			MMDeviceEnumerator? enumerator = _enumerator;
			if (enumerator != null)
			{
				enumerator.Dispose();
			}
			_enumerator = null;
		}
	}

	private void ReleaseEndpointNotifications()
	{
		if (_enumerator != null)
		{
			try
			{
				_enumerator.UnregisterEndpointNotificationCallback((IMMNotificationClient)(object)this);
			}
			catch
			{
			}
			_enumerator.Dispose();
			_enumerator = null;
		}
	}

	private static void Dispatch(Action action)
	{
		Application current = Application.Current;
		Dispatcher val = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (val != null && !val.CheckAccess())
		{
			val.BeginInvoke((Delegate)action, Array.Empty<object>());
		}
		else
		{
			action();
		}
	}

	public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		if ((int)flow == 0 && (int)role == 0)
		{
			Dispatch(ReconcileMuteState);
		}
	}

	public void OnDeviceStateChanged(string deviceId, DeviceState newState)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Invalid comparison between Unknown and I4
		if ((int)newState != 1)
		{
			ReconcileIfLeasedDevice(deviceId);
		}
	}

	public void OnDeviceAdded(string pwstrDeviceId)
	{
	}

	public void OnDeviceRemoved(string deviceId)
	{
		ReconcileIfLeasedDevice(deviceId);
	}

	public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
	{
	}

	private void ReconcileIfLeasedDevice(string deviceId)
	{
		Dispatch(delegate
		{
			if (_lease != null && string.Equals(_lease.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
			{
				ReconcileMuteState();
			}
		});
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			if (_started)
			{
				AirPlayStreamer.Instance.LogReceived -= OnBackendLogReceived;
			}
			_state.Clear();
			RestoreLease();
			ReleaseEndpointNotifications();
		}
	}
}
