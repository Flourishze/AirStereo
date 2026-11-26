using System.Collections.Generic;
using AirStereo2.Models;
using AirStereo2.ViewModels;

namespace AirStereo2.Services;

public static class TestedDeviceIcon
{
	public static DeviceIconKind Resolve(IReadOnlyList<DeviceViewModel> devices)
	{
		return devices.Count switch
		{
			0 => DeviceIconKind.HifiSpeaker, 
			1 => devices[0].IconKind, 
			_ => DeviceGroupViewModel.ResolveGroupIcon(devices), 
		};
	}
}
