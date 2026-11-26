namespace AirStereo2.Services;

public static class DeviceConnectionMachine
{
	public static DeviceTransition? Apply(DeviceConnectionState state, BackendEvent evt, bool silentStartup)
	{
		bool flag;
		switch (evt.Tag)
		{
		case "Status":
		case "EndpointStatus":
		case "Auth":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return null;
		}
		switch (evt.Kind)
		{
		case "CONNECTED":
			return new DeviceTransition(state with
			{
				IsConnected = true,
				IsConnecting = false,
				IsStandby = false,
				IsPreempted = false,
				IsPinRequested = false,
				IsCalibrationSlow = false,
				PreserveReceiverVolume = false
			}, (DeviceEffects)(0x1B | ((!state.PreserveReceiverVolume) ? 32 : 0)));
		case "FAILED":
			if (evt.Tag != "Auth")
			{
				return new DeviceTransition(state with
				{
					IsConnected = false,
					IsConnecting = false,
					IsStandby = false,
					IsPreempted = false,
					IsPinRequested = false,
					IsCalibrationSlow = false,
					PreserveReceiverVolume = false
				}, DeviceEffects.StopConnectTimeout | DeviceEffects.ClearCalibrationText | DeviceEffects.FailFromBackend | DeviceEffects.StopBackendRetries | DeviceEffects.AddNetworkHint, evt.GetString("reason") ?? string.Empty);
			}
			break;
		case "DISCONNECTED":
			if (!state.IsConnected && !state.IsConnecting)
			{
				return new DeviceTransition(state, DeviceEffects.ClearCalibrationText);
			}
			return new DeviceTransition(state with
			{
				IsConnected = false,
				IsConnecting = false,
				IsStandby = false,
				PreserveReceiverVolume = false
			}, DeviceEffects.StopConnectTimeout | DeviceEffects.ClearCalibrationText);
		case "PREEMPTED":
			return new DeviceTransition(state with
			{
				IsPreempted = true
			}, DeviceEffects.None);
		case "RECONNECTING":
			return new DeviceTransition(state with
			{
				IsConnected = false,
				IsConnecting = true,
				IsStandby = false
			}, DeviceEffects.ClearCalibrationText | DeviceEffects.StartConnectTimeout);
		case "CALIBRATING":
			return new DeviceTransition(state with
			{
				IsConnected = false,
				IsConnecting = true
			}, DeviceEffects.ClearCalibrationText | DeviceEffects.StartConnectTimeout);
		case "CALIBRATION_SLOW":
			return new DeviceTransition(state with
			{
				IsConnected = false,
				IsConnecting = true,
				IsCalibrationSlow = true
			}, DeviceEffects.ShowCalibrationSlowText | DeviceEffects.StartConnectTimeout);
		case "PIN_REQUIRED":
			return new DeviceTransition(state with
			{
				IsConnecting = false,
				IsPinRequested = true
			}, (DeviceEffects)(0xB | (silentStartup ? 640 : 0)));
		case "PIN_FAILED":
			return new DeviceTransition(state with
			{
				IsPinRequested = false,
				IsConnecting = false,
				IsConnected = false
			}, (DeviceEffects)(0x283 | ((!silentStartup) ? 256 : 0)));
		case "PASSWORD_REQUIRED":
		case "PASSWORD_FAILED":
			return new DeviceTransition(state, DeviceEffects.RequestAccessPassword, null, evt.GetString("ip"), evt.Kind == "PASSWORD_FAILED");
		}
		return null;
	}
}
