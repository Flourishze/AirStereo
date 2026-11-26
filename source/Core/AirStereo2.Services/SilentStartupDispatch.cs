using System;
using System.Threading.Tasks;

namespace AirStereo2.Services;

public static class SilentStartupDispatch
{
	public static Func<T> MarshalSnapshot<T>(Func<T> get, Func<Func<T>, T> invokeOnUi)
	{
		ArgumentNullException.ThrowIfNull(get, "get");
		ArgumentNullException.ThrowIfNull(invokeOnUi, "invokeOnUi");
		return () => invokeOnUi(get);
	}

	public static Func<TArg, Task> MarshalAsync<TArg>(Func<TArg, Task> action, Func<Func<Task>, Task> invokeOnUiAsync)
	{
		ArgumentNullException.ThrowIfNull(action, "action");
		ArgumentNullException.ThrowIfNull(invokeOnUiAsync, "invokeOnUiAsync");
		return (TArg arg) => invokeOnUiAsync(() => action(arg));
	}
}
