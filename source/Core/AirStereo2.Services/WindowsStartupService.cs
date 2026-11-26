using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace AirStereo2.Services;

public sealed class WindowsStartupService
{
	public const string AppRunValueName = "AirStereo2";

	public const string PackagedStartupTaskId = "AirStereo2StartupTask";

	public const string SilentStartArg = "--silent-start";

	public const string LegacyMinimizedArg = "--minimized";

	private readonly IRegistryRunStore _store;

	public WindowsStartupService(IRegistryRunStore? store = null)
	{
		_store = store ?? new HkcuRunRegistryStore();
	}

	public static string ResolveExecutablePath()
	{
		try
		{
			string processPath = Environment.ProcessPath;
			if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
			{
				return processPath;
			}
		}
		catch
		{
		}
		try
		{
			string text = Process.GetCurrentProcess().MainModule?.FileName;
			if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
			{
				return text;
			}
		}
		catch
		{
		}
		string location = Assembly.GetExecutingAssembly().Location;
		if (!string.IsNullOrWhiteSpace(location))
		{
			if (location.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			{
				string text2 = Path.ChangeExtension(location, ".exe");
				if (File.Exists(text2))
				{
					return text2;
				}
			}
			return location;
		}
		return string.Empty;
	}

	public static string BuildCommandLine(string executablePath, bool silentStart = true)
	{
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			return string.Empty;
		}
		string text = (executablePath.Contains(' ', StringComparison.Ordinal) ? ("\"" + executablePath + "\"") : executablePath);
		if (!silentStart)
		{
			return text;
		}
		return text + " --silent-start";
	}

	public bool IsRegistered()
	{
		return !string.IsNullOrWhiteSpace(_store.GetValue("AirStereo2"));
	}

	public async Task<bool> IsRegisteredAsync()
	{
		if (!IsPackaged())
		{
			return IsRegistered();
		}
		try
		{
			_store.DeleteValue("AirStereo2");
			StartupTaskState state = (await WindowsRuntimeSystemExtensions.AsTask<StartupTask>(StartupTask.GetAsync("AirStereo2StartupTask")).ConfigureAwait(continueOnCapturedContext: false)).State;
			return ((int)state == 2 || (int)state == 4) ? true : false;
		}
		catch
		{
			return false;
		}
	}

	public bool TryEnable(out string? error)
	{
		error = null;
		try
		{
			string text = ResolveExecutablePath();
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				error = "executable_not_found";
				return false;
			}
			string commandLine = BuildCommandLine(text);
			_store.SetValue("AirStereo2", commandLine);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	public async Task<(bool Success, string? Error)> TryEnableAsync()
	{
		string error;
		if (!IsPackaged())
		{
			return (Success: TryEnable(out error), Error: error);
		}
		try
		{
			_store.DeleteValue("AirStereo2");
			StartupTask val = await WindowsRuntimeSystemExtensions.AsTask<StartupTask>(StartupTask.GetAsync("AirStereo2StartupTask")).ConfigureAwait(continueOnCapturedContext: false);
			StartupTaskState state = val.State;
			switch ((int)state)
			{
			case 2:
			case 4:
				return (Success: true, Error: null);
			case 1:
				return (Success: false, Error: "startup_disabled_by_user");
			case 3:
				return (Success: false, Error: "startup_disabled_by_policy");
			default:
			{
				StartupTaskState val2 = await WindowsRuntimeSystemExtensions.AsTask<StartupTaskState>(val.RequestEnableAsync()).ConfigureAwait(continueOnCapturedContext: false);
				bool flag = (((int)val2 == 2 || (int)val2 == 4) ? true : false);
				return flag ? (Success: true, Error: null) : (Success: false, Error: val2.ToString());
			}
			}
		}
		catch (Exception ex)
		{
			return (Success: false, Error: ex.Message);
		}
	}

	public bool TryDisable(out string? error)
	{
		error = null;
		try
		{
			_store.DeleteValue("AirStereo2");
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	public async Task<(bool Success, string? Error)> TryDisableAsync()
	{
		string error;
		if (!IsPackaged())
		{
			return (Success: TryDisable(out error), Error: error);
		}
		try
		{
			(await WindowsRuntimeSystemExtensions.AsTask<StartupTask>(StartupTask.GetAsync("AirStereo2StartupTask")).ConfigureAwait(continueOnCapturedContext: false)).Disable();
			return (Success: true, Error: null);
		}
		catch (Exception ex)
		{
			return (Success: false, Error: ex.Message);
		}
	}

	public static bool IsPackaged()
	{
		try
		{
			_ = Package.Current.Id.Name;
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsSilentStart(string[]? args)
	{
		if (args == null || args.Length == 0)
		{
			return false;
		}
		foreach (string a in args)
		{
			if (string.Equals(a, "--silent-start", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}
}
