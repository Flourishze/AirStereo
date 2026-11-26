using System;
using System.Threading;

namespace AirStereo2.Services;

public sealed class SingleInstanceGuard : IDisposable
{
	internal const string DefaultName = "AirStereo2.SingleInstance.6F1C2E7A";

	private readonly Mutex _mutex;

	private readonly EventWaitHandle _activateEvent;

	private readonly RegisteredWaitHandle _registration;

	private bool _disposed;

	private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, Action onActivate)
	{
		_mutex = mutex;
		_activateEvent = activateEvent;
		_registration = ThreadPool.RegisterWaitForSingleObject(activateEvent, delegate
		{
			onActivate();
		}, null, -1, executeOnlyOnce: false);
	}

	public static SingleInstanceGuard? TryAcquire(Action onActivate, bool activateExisting, string name = "AirStereo2.SingleInstance.6F1C2E7A")
	{
		ArgumentNullException.ThrowIfNull(onActivate, "onActivate");
		Mutex mutex = new Mutex(initiallyOwned: false, "Local\\" + name + ".Mutex");
		bool flag;
		try
		{
			flag = mutex.WaitOne(0);
		}
		catch (AbandonedMutexException)
		{
			flag = true;
		}
		EventWaitHandle eventWaitHandle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, "Local\\" + name + ".Activate");
		if (!flag)
		{
			try
			{
				if (activateExisting)
				{
					eventWaitHandle.Set();
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				eventWaitHandle.Dispose();
				mutex.Dispose();
			}
			return null;
		}
		return new SingleInstanceGuard(mutex, eventWaitHandle, onActivate);
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_registration.Unregister(null);
			try
			{
				_mutex.ReleaseMutex();
			}
			catch (ApplicationException)
			{
			}
			_mutex.Dispose();
			_activateEvent.Dispose();
		}
	}
}
