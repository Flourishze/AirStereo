using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirStereo2.Services;

public sealed class AudioCaptureService : IDisposable
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry32
	{
		public uint dwSize;

		public uint cntUsage;

		public uint th32ProcessID;

		public nint th32DefaultHeapID;

		public uint th32ModuleID;

		public uint cntThreads;

		public uint th32ParentProcessID;

		public int pcPriClassBase;

		public uint dwFlags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	private const uint Th32csSnapProcess = 2u;

	private const uint ProcessQueryLimitedInformation = 4096u;

	private static readonly nint InvalidHandleValue = new IntPtr(-1);

	private static readonly string[] VirtualDeviceKeywords = new string[6] { "cable", "vb-audio", "virtual", "voicemeeter", "blackhole", "virtual audio" };

	public static bool WasCreated { get; private set; }

	public AudioCaptureService()
	{
		WasCreated = true;
	}

	public bool ProbeDefaultDeviceOccupied()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Expected O, but got Unknown
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		try
		{
			MMDeviceEnumerator val = new MMDeviceEnumerator();
			try
			{
				MMDevice defaultAudioEndpoint = val.GetDefaultAudioEndpoint((DataFlow)0, (Role)1);
				try
				{
					WasapiLoopbackCapture val2 = new WasapiLoopbackCapture(defaultAudioEndpoint);
					try
					{
						return false;
					}
					finally
					{
						((IDisposable)val2)?.Dispose();
					}
				}
				finally
				{
					((IDisposable)defaultAudioEndpoint)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch (COMException ex) when (ex.ErrorCode == -2004287478)
		{
			return true;
		}
		catch
		{
			return false;
		}
	}

	public List<AudioDeviceItem> EnumerateVirtualDevices()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Expected O, but got Unknown
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Invalid comparison between Unknown and I4
		List<AudioDeviceItem> list = new List<AudioDeviceItem>();
		try
		{
			MMDeviceEnumerator val = new MMDeviceEnumerator();
			try
			{
				DataFlow[] array = (DataFlow[])(object)new DataFlow[2]
				{
					default(DataFlow),
					(DataFlow)1
				};
				foreach (DataFlow val2 in array)
				{
					foreach (MMDevice item in val.EnumerateAudioEndPoints(val2, (DeviceState)1))
					{
						try
						{
							string text = item.FriendlyName ?? "";
							string lower = text.ToLowerInvariant();
							if (VirtualDeviceKeywords.Any((string k) => lower.Contains(k)))
							{
								list.Add(new AudioDeviceItem
								{
									DeviceId = item.ID,
									Name = text,
									Flow = (((int)val2 == 0) ? "Loopback" : "Capture"),
									IsLoopback = ((int)val2 == 0)
								});
							}
						}
						finally
						{
							item.Dispose();
						}
					}
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch
		{
		}
		return list;
	}

	public List<AudioDeviceItem> EnumerateAllDevices()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Expected O, but got Unknown
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Invalid comparison between Unknown and I4
		List<AudioDeviceItem> list = new List<AudioDeviceItem>();
		try
		{
			MMDeviceEnumerator val = new MMDeviceEnumerator();
			try
			{
				DataFlow[] array = (DataFlow[])(object)new DataFlow[2]
				{
					default(DataFlow),
					(DataFlow)1
				};
				foreach (DataFlow val2 in array)
				{
					foreach (MMDevice item in val.EnumerateAudioEndPoints(val2, (DeviceState)1))
					{
						try
						{
							list.Add(new AudioDeviceItem
							{
								DeviceId = item.ID,
								Name = (item.FriendlyName ?? "Unknown"),
								Flow = (((int)val2 == 0) ? "Loopback" : "Capture"),
								IsLoopback = ((int)val2 == 0)
							});
						}
						finally
						{
							item.Dispose();
						}
					}
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch
		{
		}
		return list;
	}

	public List<ProcessItem> EnumerateAudioProcesses()
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected O, but got Unknown
		List<ProcessItem> list = new List<ProcessItem>();
		HashSet<int> hashSet = new HashSet<int>();
		Dictionary<int, int> parentByPid = SnapshotParentProcessIds();
		Process[] array;
		try
		{
			array = Process.GetProcesses();
		}
		catch
		{
			array = Array.Empty<Process>();
		}
		try
		{
			MMDeviceEnumerator val = new MMDeviceEnumerator();
			try
			{
				MMDevice defaultAudioEndpoint = val.GetDefaultAudioEndpoint((DataFlow)0, (Role)1);
				try
				{
					SessionCollection sessions = defaultAudioEndpoint.AudioSessionManager.Sessions;
					for (int i = 0; i < sessions.Count; i++)
					{
						AudioSessionControl val2 = sessions[i];
						try
						{
							uint getProcessID = val2.GetProcessID;
							if (getProcessID == 0)
							{
								continue;
							}
							using Process process = Process.GetProcessById((int)getProcessID);
							int num = FindCaptureRootProcessId(process, parentByPid);
							List<int> captureIds = BuildCaptureCandidateIds(process, parentByPid, array);
							int item = (int)getProcessID;
							if (!hashSet.Contains(item) && !hashSet.Contains(num))
							{
								hashSet.Add(item);
								hashSet.Add(num);
								string processName = process.ProcessName;
								string sessionDisplay = null;
								try
								{
									sessionDisplay = val2.DisplayName;
								}
								catch
								{
								}
								string processPathById = GetProcessPathById(num);
								list.Add(CreateProcessItem(num, (int)getProcessID, captureIds, processName, processPathById, sessionDisplay));
							}
						}
						catch
						{
						}
						finally
						{
							try
							{
								val2.Dispose();
							}
							catch
							{
							}
						}
					}
				}
				finally
				{
					((IDisposable)defaultAudioEndpoint)?.Dispose();
				}
			}
			finally
			{
				((IDisposable)val)?.Dispose();
			}
		}
		catch
		{
		}
		try
		{
			string[] array2 = new string[14]
			{
				"spotify", "chrome", "firefox", "msedge", "vlc", "foobar2000", "aimp", "musicbee", "iTunes", "wmplayer",
				"AppleMusic", "Music", "Music.UI", "AMPLibraryAgent"
			};
			for (int j = 0; j < array2.Length; j++)
			{
				Process[] processesByName = Process.GetProcessesByName(array2[j]);
				foreach (Process process2 in processesByName)
				{
					try
					{
						int num2 = FindCaptureRootProcessId(process2, parentByPid);
						List<int> captureIds2 = BuildCaptureCandidateIds(process2, parentByPid, array);
						if (!hashSet.Contains(process2.Id) && !hashSet.Contains(num2))
						{
							hashSet.Add(process2.Id);
							hashSet.Add(num2);
							string processPathById2 = GetProcessPathById(num2);
							list.Add(CreateProcessItem(num2, process2.Id, captureIds2, process2.ProcessName, processPathById2, null));
						}
					}
					catch
					{
					}
					finally
					{
						process2.Dispose();
					}
				}
			}
		}
		finally
		{
			Process[] processesByName = array;
			foreach (Process process3 in processesByName)
			{
				try
				{
					process3.Dispose();
				}
				catch
				{
				}
			}
		}
		return list.OrderBy<ProcessItem, string>((ProcessItem p) => p.ApplicationName, StringComparer.CurrentCultureIgnoreCase).ThenBy<ProcessItem, string>((ProcessItem p) => p.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public ProcessItem? FindRunningProcessByIdentity(string? executablePath, string? processName)
	{
		string text = executablePath?.Trim() ?? string.Empty;
		string text2 = processName?.Trim() ?? string.Empty;
		if (text.Length == 0 && text2.Length == 0)
		{
			return null;
		}
		Process[] processes;
		try
		{
			processes = Process.GetProcesses();
		}
		catch
		{
			return null;
		}
		Dictionary<int, int> parentByPid = SnapshotParentProcessIds();
		ProcessItem processItem = null;
		try
		{
			Process[] array = processes;
			foreach (Process process in array)
			{
				try
				{
					int processId = FindCaptureRootProcessId(process, parentByPid);
					string processPathById = GetProcessPathById(processId);
					string processName2 = process.ProcessName;
					bool flag = text.Length > 0 && string.Equals(processPathById, text, StringComparison.OrdinalIgnoreCase);
					bool flag2 = text2.Length > 0 && string.Equals(processName2, text2, StringComparison.OrdinalIgnoreCase);
					if (!((text.Length > 0) ? (!flag) : (!flag2)))
					{
						List<int> captureIds = BuildCaptureCandidateIds(process, parentByPid, processes);
						ProcessItem processItem2 = CreateProcessItem(processId, process.Id, captureIds, processName2, processPathById, null);
						if (flag)
						{
							return processItem2;
						}
						if (processItem == null && flag2)
						{
							processItem = processItem2;
						}
					}
				}
				catch
				{
				}
			}
			return processItem;
		}
		finally
		{
			Process[] array = processes;
			foreach (Process process2 in array)
			{
				try
				{
					process2.Dispose();
				}
				catch
				{
				}
			}
		}
	}

	private static ProcessItem CreateProcessItem(int processId, int sessionPid, List<int> captureIds, string processName, string executablePath, string? sessionDisplay)
	{
		(string? Description, string? ProductName, ImageSource? Icon) tuple = ProcessMetadataCache.Get(executablePath);
		string item = tuple.Description;
		string item2 = tuple.ProductName;
		ImageSource item3 = tuple.Icon;
		string applicationName = ProcessDisplayName.ResolveApplicationName(processName, item, item2, sessionDisplay);
		return new ProcessItem
		{
			ProcessId = processId,
			SessionProcessId = sessionPid,
			CaptureProcessIds = captureIds,
			ProcessName = processName,
			ApplicationName = applicationName,
			ExecutablePath = (executablePath ?? ""),
			Icon = item3
		};
	}

	private static string GetProcessPath(Process proc)
	{
		try
		{
			return proc.MainModule?.FileName ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static string GetProcessPathById(int processId)
	{
		try
		{
			using Process proc = Process.GetProcessById(processId);
			return GetProcessPath(proc);
		}
		catch
		{
			return "";
		}
	}

	private static int FindCaptureRootProcessId(Process process, Dictionary<int, int> parentByPid)
	{
		int num = process.Id;
		string processName = process.ProcessName;
		string text = GetProcessPath(process);
		int value;
		for (int i = 0; i < 8 && parentByPid.TryGetValue(num, out value) && value > 0; i++)
		{
			try
			{
				using Process process2 = Process.GetProcessById(value);
				string processPath = GetProcessPath(process2);
				bool num2 = !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(processPath) && string.Equals(text, processPath, StringComparison.OrdinalIgnoreCase);
				bool flag = string.Equals(processName, process2.ProcessName, StringComparison.OrdinalIgnoreCase);
				if (!num2 && !flag)
				{
					break;
				}
				num = value;
				processName = process2.ProcessName;
				text = processPath;
				continue;
			}
			catch
			{
			}
			break;
		}
		return num;
	}

	private static List<int> BuildCaptureCandidateIds(Process process, Dictionary<int, int> parentByPid, IReadOnlyList<Process> allProcesses)
	{
		List<int> list = new List<int>();
		AddCandidate(list, process.Id);
		AddCandidate(list, FindCaptureRootProcessId(process, parentByPid));
		AddDescendants(list, process.Id, parentByPid, 3);
		AddSamePackageProcesses(list, process.Id, allProcesses);
		if (parentByPid.TryGetValue(process.Id, out var value) && IsUwpHostProcess(value))
		{
			AddCandidate(list, value);
			AddDescendants(list, value, parentByPid, 3);
			AddSamePackageProcesses(list, value, allProcesses);
		}
		return list;
	}

	private static void AddSamePackageProcesses(List<int> candidates, int processId, IReadOnlyList<Process> allProcesses)
	{
		string packageFullNameByPid = GetPackageFullNameByPid(processId);
		if (string.IsNullOrWhiteSpace(packageFullNameByPid))
		{
			return;
		}
		foreach (Process allProcess in allProcesses)
		{
			try
			{
				if (string.Equals(GetPackageFullNameByPid(allProcess.Id), packageFullNameByPid, StringComparison.OrdinalIgnoreCase))
				{
					AddCandidate(candidates, allProcess.Id);
				}
			}
			catch
			{
			}
			if (candidates.Count >= 32)
			{
				break;
			}
		}
	}

	private static string GetPackageFullNameByPid(int processId)
	{
		nint num = OpenProcess(4096u, bInheritHandle: false, (uint)processId);
		if (num == IntPtr.Zero)
		{
			return string.Empty;
		}
		try
		{
			int packageFullNameLength = 0;
			GetPackageFullName(num, ref packageFullNameLength, null);
			if (packageFullNameLength <= 0)
			{
				return string.Empty;
			}
			StringBuilder stringBuilder = new StringBuilder(packageFullNameLength);
			return (GetPackageFullName(num, ref packageFullNameLength, stringBuilder) == 0) ? stringBuilder.ToString() : string.Empty;
		}
		catch
		{
			return string.Empty;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	private static bool IsUwpHostProcess(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			return process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("WWAHost", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static void AddDescendants(List<int> candidates, int rootPid, Dictionary<int, int> parentByPid, int maxDepth)
	{
		Queue<(int, int)> queue = new Queue<(int, int)>();
		queue.Enqueue((rootPid, 0));
		while (queue.Count > 0 && candidates.Count < 24)
		{
			var (pid, num) = queue.Dequeue();
			if (num >= maxDepth)
			{
				continue;
			}
			foreach (int item in from kv in parentByPid
				where kv.Value == pid
				select kv.Key)
			{
				AddCandidate(candidates, item);
				queue.Enqueue((item, num + 1));
				if (candidates.Count >= 24)
				{
					break;
				}
			}
		}
	}

	private static void AddCandidate(List<int> candidates, int pid)
	{
		if (pid > 0 && !candidates.Contains(pid))
		{
			candidates.Add(pid);
		}
	}

	private static Dictionary<int, int> SnapshotParentProcessIds()
	{
		Dictionary<int, int> dictionary = new Dictionary<int, int>();
		nint num = CreateToolhelp32Snapshot(2u, 0u);
		if (num == InvalidHandleValue)
		{
			return dictionary;
		}
		try
		{
			ProcessEntry32 lppe = new ProcessEntry32
			{
				dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
			};
			if (!Process32First(num, ref lppe))
			{
				return dictionary;
			}
			do
			{
				dictionary[(int)lppe.th32ProcessID] = (int)lppe.th32ParentProcessID;
			}
			while (Process32Next(num, ref lppe));
			return dictionary;
		}
		finally
		{
			CloseHandle(num);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32First(nint hSnapshot, ref ProcessEntry32 lppe);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32Next(nint hSnapshot, ref ProcessEntry32 lppe);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetPackageFullName(nint hProcess, ref int packageFullNameLength, StringBuilder? packageFullName);

	public void Dispose()
	{
	}
}
