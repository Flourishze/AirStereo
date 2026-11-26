using System;
using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed class DeviceEventScope
{
	private readonly Func<string, bool> _isAddress;

	private readonly string? _endpointId;

	public DeviceEventScope(Func<string, bool> isAddress, string? endpointId)
	{
		_isAddress = isAddress;
		_endpointId = endpointId;
	}

	public static DeviceEventScope ForAddresses(IEnumerable<string> addresses, string? endpointId = null)
	{
		return new DeviceEventScope(new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase).Contains, endpointId);
	}

	public DeviceEventRoute Route(BackendEvent evt)
	{
		if (evt.Is("Latency", "OVERRIDE_APPLIED"))
		{
			return DeviceEventRoute.ResetLatencySmoothing;
		}
		bool flag = evt.Tag == "Codec";
		if (flag)
		{
			string kind = evt.Kind;
			bool flag2 = ((kind == "ACTIVE" || kind == "REQUESTED") ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			return DeviceEventRoute.Codec;
		}
		if (!Concerns(evt))
		{
			return DeviceEventRoute.Ignore;
		}
		switch (evt.Tag)
		{
		case "Volume":
			return DeviceEventRoute.Volume;
		case "Latency":
			return DeviceEventRoute.Latency;
		case "Codec":
			return DeviceEventRoute.Codec;
		case "Status":
		case "EndpointStatus":
		case "Auth":
			return DeviceEventRoute.Connection;
		default:
			return DeviceEventRoute.Ignore;
		}
	}

	public bool Concerns(BackendEvent evt)
	{
		if (evt.Tag == "EndpointStatus" || (evt.Tag == "Codec" && evt.Kind == "EXACT_ENDPOINT_ACTIVE"))
		{
			string? evEp = evt.GetString("endpoint_id");
			if (!string.IsNullOrWhiteSpace(_endpointId) && !string.IsNullOrWhiteSpace(evEp))
			{
				if (string.Equals(evEp, _endpointId, StringComparison.Ordinal))
				{
					return true;
				}
				string bareLocal = StripEndpointPrefix(_endpointId);
				string bareEvent = StripEndpointPrefix(evEp);
				if (string.Equals(bareEvent, bareLocal, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			string? epIp = evt.GetString("ip");
			if (!string.IsNullOrWhiteSpace(epIp) && _isAddress(epIp))
			{
				return true;
			}
			return false;
		}
		string text = evt.GetString("ip");
		if (text != null)
		{
			return _isAddress(text);
		}
		return false;
	}

	private static string StripEndpointPrefix(string value)
	{
		if (value.StartsWith("raop:", StringComparison.OrdinalIgnoreCase)) return value.Substring(5);
		if (value.StartsWith("airplay:", StringComparison.OrdinalIgnoreCase)) return value.Substring(8);
		return value;
	}
}
