using System;
using System.Collections.Generic;
using AirStereo2.Messages;

namespace AirStereo2.Services;

public static class VolumeCoordination
{
	private sealed class ApplyScope : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;
				_applyDepth--;
			}
		}
	}

	[ThreadStatic]
	private static int _applyDepth;

	public static bool IsApplyingVolume => _applyDepth > 0;

	public static IDisposable BeginApply()
	{
		_applyDepth++;
		return new ApplyScope();
	}

	public static int ClampVolume(int volume)
	{
		return Math.Clamp(volume, 0, 100);
	}

	public static int ClampVolume(double volume)
	{
		return ClampVolume((int)Math.Round(volume));
	}

	public static bool ShouldPreserveReceiverVolumeOnConnect(bool isHomePod, bool anotherOutputActive, int targetCount)
	{
		if (isHomePod && !anotherOutputActive)
		{
			return targetCount == 1;
		}
		return false;
	}

	public static bool TryReadReceiverVolume(BackendEvent evt, out string ip, out int volume)
	{
		ip = string.Empty;
		volume = 0;
		string text = default(string);
		long valueOrDefault = default(long);
		int num2;
		if (evt.Is("Volume", "CURRENT"))
		{
			text = evt.GetString("ip");
			if (text != null && text.Length > 0)
			{
				long? @int = evt.GetInt64("percent");
				int num;
				if (@int.HasValue)
				{
					valueOrDefault = @int.GetValueOrDefault();
					num = 1;
				}
				else
				{
					num = 0;
				}
				num2 = ((num == 0) ? 1 : 0);
				goto IL_005b;
			}
		}
		num2 = 1;
		goto IL_005b;
		IL_005b:
		bool flag = (byte)num2 != 0;
		if (!flag)
		{
			bool flag2 = ((valueOrDefault < 0 || valueOrDefault > 100) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			return false;
		}
		ip = text;
		volume = (int)valueOrDefault;
		return true;
	}

	public static bool ShouldApplyMasterToDevice(VolumeChangeOrigin origin)
	{
		return origin == VolumeChangeOrigin.Master;
	}

	public static bool ShouldUpdateMasterFromDevice(VolumeChangeOrigin origin, int connectedLogicalCount)
	{
		return origin switch
		{
			VolumeChangeOrigin.Device => connectedLogicalCount == 1, 
			VolumeChangeOrigin.Group => true, 
			_ => false, 
		};
	}

	public static bool ShouldSendBackendFromLocalVolumeChange(bool suppressSideEffects, bool isConnected)
	{
		return !suppressSideEffects && isConnected;
	}

	public static bool ShouldSendBackendAfterMasterApply(bool isConnected)
	{
		return isConnected;
	}

	public static bool ShouldNotifyMasterOfDeviceVolume(bool suppressSideEffects, bool isConnected)
	{
		return !suppressSideEffects && isConnected;
	}

	public static IReadOnlyList<string> DedupeTargetIps(IEnumerable<string> ips)
	{
		List<string> list = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string ip in ips)
		{
			if (!string.IsNullOrWhiteSpace(ip))
			{
				string item = ip.Trim();
				if (hashSet.Add(item))
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	public static bool ShouldBroadcastMasterToDevices(VolumeChangeOrigin? applyingOrigin)
	{
		if (!applyingOrigin.HasValue || applyingOrigin.GetValueOrDefault() == VolumeChangeOrigin.Master)
		{
			return true;
		}
		return false;
	}
}
