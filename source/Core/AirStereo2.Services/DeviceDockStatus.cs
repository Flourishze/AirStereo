namespace AirStereo2.Services;

public static class DeviceDockStatus
{
	public static DockStatusKind Resolve(bool isFailed, bool isPinRequested, bool isConnecting, bool isConnected, bool isStandby)
	{
		if (isFailed || isPinRequested)
		{
			return DockStatusKind.Failed;
		}
		if (isConnecting)
		{
			return DockStatusKind.Connecting;
		}
		if (isConnected)
		{
			if (!isStandby)
			{
				return DockStatusKind.Active;
			}
			return DockStatusKind.Standby;
		}
		return DockStatusKind.Disconnected;
	}

	public static string ResourceKey(DockStatusKind kind)
	{
		return kind switch
		{
			DockStatusKind.Failed => "Dock_StatusAttention", 
			DockStatusKind.Connecting => "Dock_StatusConnecting", 
			DockStatusKind.Standby => "Dock_StatusStandby", 
			DockStatusKind.Active => "Dock_StatusActive", 
			_ => "Dock_StatusDisconnected", 
		};
	}
}
