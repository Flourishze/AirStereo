using System;

namespace AirStereo2.Services;

internal sealed class AppEntryRefreshGate
{
	private readonly object _sync = new object();

	private bool _workerRunning;

	private bool _pending;

	private nint _latestOwnerWindow;

	public bool Request(nint ownerWindow)
	{
		if (ownerWindow == IntPtr.Zero)
		{
			throw new ArgumentException("A valid owner window handle is required.", "ownerWindow");
		}
		lock (_sync)
		{
			_latestOwnerWindow = ownerWindow;
			_pending = true;
			if (_workerRunning)
			{
				return false;
			}
			_workerRunning = true;
			return true;
		}
	}

	public bool TryTakeNext(out nint ownerWindow)
	{
		lock (_sync)
		{
			if (!_pending)
			{
				_workerRunning = false;
				ownerWindow = IntPtr.Zero;
				return false;
			}
			ownerWindow = _latestOwnerWindow;
			_pending = false;
			return true;
		}
	}
}
