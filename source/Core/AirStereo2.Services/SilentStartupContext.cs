using System;
using System.Threading;

namespace AirStereo2.Services;

public static class SilentStartupContext
{
	private sealed class Scope : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				Interlocked.Decrement(ref _activeScopes);
			}
		}
	}

	private static int _activeScopes;

	public static bool SuppressUiDialogs => Volatile.Read(in _activeScopes) > 0;

	public static bool IsActive => Volatile.Read(in _activeScopes) > 0;

	public static IDisposable Enter()
	{
		Interlocked.Increment(ref _activeScopes);
		return new Scope();
	}
}
