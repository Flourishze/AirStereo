using System;
using System.Collections.Generic;

namespace AirStereo2.Services;

internal sealed class LocalOutputMuteState
{
	private readonly HashSet<string> _connectedReceivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public bool Enabled { get; private set; }

	public bool ShouldMute
	{
		get
		{
			if (Enabled)
			{
				return _connectedReceivers.Count > 0;
			}
			return false;
		}
	}

	public int ConnectedCount => _connectedReceivers.Count;

	public void SetEnabled(bool enabled)
	{
		Enabled = enabled;
	}

	public void ApplyBackendLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}
		string[] array = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length < 2)
		{
			return;
		}
		if (array[0] == "[EndpointStatus]")
		{
			switch (array[1])
			{
			case "CONNECTED":
				if (array.Length >= 4)
				{
					_connectedReceivers.Add(array[2]);
				}
				break;
			case "DISCONNECTED":
			case "FAILED":
				if (array.Length >= 4)
				{
					_connectedReceivers.Remove(array[2]);
				}
				break;
			}
		}
		else
		{
			if (array[0] != "[Status]")
			{
				return;
			}
			switch (array[1])
			{
			case "CONNECTED":
				if (array.Length >= 3)
				{
					_connectedReceivers.Add(array[2]);
				}
				break;
			case "DISCONNECTED":
			case "FAILED":
				if (array.Length >= 3)
				{
					_connectedReceivers.Remove(array[2]);
				}
				break;
			case "BACKEND_EXITED":
			case "BACKEND_RECOVERY_FAILED":
				_connectedReceivers.Clear();
				break;
			}
		}
	}

	public void Clear()
	{
		_connectedReceivers.Clear();
	}
}
