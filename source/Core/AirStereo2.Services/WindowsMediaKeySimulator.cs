using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirStereo2.Services;

public static class WindowsMediaKeySimulator
{
	private struct INPUT
	{
		public uint type;

		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)]
		public KEYBDINPUT ki;
	}

	private struct KEYBDINPUT
	{
		public ushort wVk;

		public ushort wScan;

		public uint dwFlags;

		public uint time;

		public nuint dwExtraInfo;
	}

	public const byte VK_MEDIA_NEXT_TRACK = 176;

	public const byte VK_MEDIA_PREV_TRACK = 177;

	public const byte VK_MEDIA_STOP = 178;

	public const byte VK_MEDIA_PLAY_PAUSE = 179;

	private const uint INPUT_KEYBOARD = 1u;

	internal const uint KeyEventExtendedKey = 1u;

	internal const uint KeyEventKeyUp = 2u;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint nInputs, [In][MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern uint MapVirtualKey(uint uCode, uint uMapType);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

	public static bool TryParseMediaCommand(string logLine, out byte virtualKey)
	{
		virtualKey = 0;
		if (!TryParseMediaCommandVerb(logLine, out var command))
		{
			return false;
		}
		byte b;
		switch (command)
		{
		case WindowsMediaCommand.Play:
		case WindowsMediaCommand.Pause:
		case WindowsMediaCommand.Toggle:
			b = 179;
			break;
		case WindowsMediaCommand.Next:
			b = 176;
			break;
		case WindowsMediaCommand.Previous:
			b = 177;
			break;
		case WindowsMediaCommand.Stop:
			b = 178;
			break;
		default:
			b = 0;
			break;
		}
		virtualKey = b;
		return virtualKey != 0;
	}

	public static bool TryParseMediaCommandVerb(string logLine, out WindowsMediaCommand command)
	{
		command = WindowsMediaCommand.Play;
		if (string.IsNullOrWhiteSpace(logLine))
		{
			return false;
		}
		string[] array = logLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length < 2 || array[0] != "[MediaCommand]")
		{
			return false;
		}
		string text = array[1].ToUpperInvariant();
		command = text switch
		{
			"PLAY" => WindowsMediaCommand.Play, 
			"PAUSE" => WindowsMediaCommand.Pause, 
			"TOGGLE" => WindowsMediaCommand.Toggle, 
			"NEXT" => WindowsMediaCommand.Next, 
			"PREV" => WindowsMediaCommand.Previous, 
			"STOP" => WindowsMediaCommand.Stop, 
			_ => WindowsMediaCommand.Play, 
		};
		switch (text)
		{
		case "PLAY":
		case "PAUSE":
		case "TOGGLE":
		case "NEXT":
		case "PREV":
		case "STOP":
			return true;
		default:
			return false;
		}
	}

	public static bool TryHandleMediaCommand(string logLine)
	{
		if (!TryParseMediaCommand(logLine, out var virtualKey))
		{
			return false;
		}
		return SendMediaKey(virtualKey);
	}

	public static bool SendMediaKey(byte vk)
	{
		try
		{
			uint num = MapVirtualKey(vk, 0u);
			INPUT iNPUT = new INPUT
			{
				type = 1u,
				U = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = vk,
						wScan = (ushort)num,
						dwFlags = 1u,
						time = 0u,
						dwExtraInfo = UIntPtr.Zero
					}
				}
			};
			INPUT iNPUT2 = new INPUT
			{
				type = 1u,
				U = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = vk,
						wScan = (ushort)num,
						dwFlags = 3u,
						time = 0u,
						dwExtraInfo = UIntPtr.Zero
					}
				}
			};
			uint num2 = SendInput(1u, new INPUT[1] { iNPUT }, Marshal.SizeOf<INPUT>());
			Thread.Sleep(25);
			uint num3 = SendInput(1u, new INPUT[1] { iNPUT2 }, Marshal.SizeOf<INPUT>());
			if (num2 != 0 && num3 != 0)
			{
				return true;
			}
			keybd_event(vk, (byte)num, 1u, UIntPtr.Zero);
			Thread.Sleep(25);
			keybd_event(vk, (byte)num, 3u, UIntPtr.Zero);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
