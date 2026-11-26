using System;
using System.Linq;

namespace AirStereo2.Services;

public static class BackendFailureFormatter
{
	public static string Format(string? rawReason, LocalizationManager? localization = null)
	{
		LocalizationManager localizationManager = localization ?? LocalizationManager.Instance;
		string text = (string.IsNullOrWhiteSpace(rawReason) ? string.Empty : rawReason.Trim());
		if (string.IsNullOrEmpty(text))
		{
			return localizationManager.GetString("Device_ConnectionFailedNoReason", "Connection failed. No detailed reason was provided by the backend.");
		}
		if (Contains(text, "AirPlay 1 devices are single-device only"))
		{
			return localizationManager.GetString("Device_AirPlay1SingleOnly", "AirPlay 1 devices are currently supported only as a single-device session. Disconnect other devices first.");
		}
		if (Contains(text, "AirPlay 2 devices cannot join an active AirPlay 1 session"))
		{
			return localizationManager.GetString("Device_AirPlay2CannotJoinAirPlay1", "An AirPlay 1 session is active. Disconnect it before adding AirPlay 2 devices.");
		}
		if (Contains(text, "Selected DNS-SD receiver instance is no longer available"))
		{
			return localizationManager.GetString("Device_SelectedInstanceUnavailable", "The selected AirPlay receiver is no longer responding or changed its network address. Wait a moment or re-select the device.");
		}
		if (Contains(text, "AirPlay group has no reachable physical audio member"))
		{
			return localizationManager.GetString("Device_NoReachableMember", "None of the devices in this AirPlay group are reachable on the network. Check Wi‑Fi connection and try again.");
		}
		if (Contains(text, "AirPlay 2 and classic RAOP cannot share one timing group"))
		{
			return localizationManager.GetString("Device_MixedTimingGroupUnsupported", "AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together. Please connect them separately.");
		}
		if (Contains(text, "Authentication rejected") || Contains(text, "authentication rejected"))
		{
			return localizationManager.GetString("Device_AuthenticationRejected", "Authentication was rejected by the device. If this is a password-protected or access-restricted speaker, check your access permissions in the Apple Home app.");
		}
		if (Contains(text, "Device does not advertise AirPlay 2 or RAOP audio"))
		{
			return localizationManager.GetString("Device_NoAirPlayAdvertisement", "This device did not advertise AirPlay 2 or AirPlay 1/RAOP audio support.");
		}
		if (Contains(text, "Password-protected AirPlay 1 devices are not supported"))
		{
			return localizationManager.GetString("Device_PasswordProtectedAirPlay1", "This AirPlay 1 device requires a password, which is not supported yet.");
		}
		if (Contains(text, "Backend recovery exhausted") || Contains(text, "BACKEND_RECOVERY_FAILED") || Contains(text, "Backend recovery"))
		{
			return localizationManager.GetString("Device_BackendRecoveryExhausted", "The AirPlay backend stopped responding after repeated recovery attempts. Try reconnecting or restart AirStereo2.");
		}
		if (Contains(text, "HomePod is playing from another AirPlay device"))
		{
			return localizationManager.GetString("Device_TakenOver", "Another device is playing on this HomePod, so AirStereo2 gave it up. Connect again to take it back.");
		}
		if (Contains(text, "Device is busy handling another AirPlay or pairing request"))
		{
			return localizationManager.GetString("Device_DeviceBusy", "The device is busy with another AirPlay or pairing request. Try again in a moment.");
		}
		if (Contains(text, "BACKEND_EXITED") || Contains(text, "backend exited"))
		{
			return localizationManager.GetString("Device_BackendExited", "The AirPlay backend process stopped unexpectedly. AirStereo2 will try to recover automatically.");
		}
		if (Contains(text, "feedback timeout") || Contains(text, "TCP probe failed") || Contains(text, "control channel") || Contains(text, "control EOF"))
		{
			return localizationManager.GetString("Device_NetworkFeedbackTimeout", "Connection lost: the device stopped responding on the network. Check Wi‑Fi and try again.");
		}
		if (Contains(text, "Receiver refused PIN pairing") && Contains(text, "HTTP 403"))
		{
			return localizationManager.GetString("Device_MacOsAirPlayAccessDenied", "The Mac refused AirPlay pairing. On the Mac, open System Settings → General → AirDrop & Handoff → AirPlay Receiver, then allow devices on the same network and check the password setting.");
		}
		if (Contains(text, "PIN") && Contains(text, "failed"))
		{
			return localizationManager.GetString("Device_PinFailed", "PIN pairing failed. Check the PIN shown on the device and try again.");
		}
		if (Contains(text, "pair-setup") || Contains(text, "pair-verify") || Contains(text, "pair setup") || Contains(text, "pair verify") || Contains(text, "SRP") || Contains(text, "HomeKit"))
		{
			return localizationManager.Format("Device_PairingFailedDetail", "Pairing failed: {0}", SanitizeDetail(text));
		}
		if (Contains(text, "auth") || Contains(text, "unauthorized") || Contains(text, "forbidden") || Contains(text, "401") || Contains(text, "403"))
		{
			return localizationManager.Format("Device_AuthFailedDetail", "Authentication failed: {0}", SanitizeDetail(text));
		}
		if (Contains(text, "RTSP") || Contains(text, "SETUP failed") || Contains(text, "setup failed"))
		{
			return localizationManager.Format("Device_RtspSetupFailedDetail", "Session setup failed: {0}", SanitizeDetail(text));
		}
		if (Contains(text, "WASAPI") || (Contains(text, "capture") && Contains(text, "failed")))
		{
			return localizationManager.Format("Device_CaptureFailedDetail", "Audio capture failed: {0}", SanitizeDetail(text));
		}
		if (Contains(text, "timeout") || Contains(text, "timed out"))
		{
			return localizationManager.Format("Device_TimeoutDetail", "Connection timed out: {0}", SanitizeDetail(text));
		}
		if (Contains(text, "refused") || Contains(text, "unreachable") || Contains(text, "network") || Contains(text, "connection reset") || Contains(text, "Broken pipe") || Contains(text, "forcibly closed"))
		{
			return localizationManager.Format("Device_NetworkErrorDetail", "Network error: {0}", SanitizeDetail(text));
		}
		return localizationManager.Format("Device_ConnectionFailed", "Connection failed: {0}", SanitizeDetail(text));
	}

	public static string FormatEqImportError(string? technical, LocalizationManager? localization = null)
	{
		LocalizationManager localizationManager = localization ?? LocalizationManager.Instance;
		string text = (string.IsNullOrWhiteSpace(technical) ? string.Empty : SanitizeDetail(technical));
		if (string.IsNullOrEmpty(text))
		{
			return localizationManager.GetString("Eq_ImportFailedGeneric", "Import failed.");
		}
		if (Contains(text, "Import file is empty"))
		{
			return localizationManager.GetString("Eq_ImportErrorEmpty", "Import file is empty.");
		}
		if (Contains(text, "Invalid JSON"))
		{
			return localizationManager.Format("Eq_ImportErrorInvalidJson", "Invalid JSON: {0}", StripPrefix(text, "Invalid JSON:"));
		}
		if (Contains(text, "did not contain a preset package"))
		{
			return localizationManager.GetString("Eq_ImportErrorNotPackage", "Import file did not contain a preset package.");
		}
		if (Contains(text, "Unsupported format"))
		{
			return localizationManager.Format("Eq_ImportErrorFormat", "Unsupported format. {0}", text);
		}
		if (Contains(text, "schemaVersion"))
		{
			return localizationManager.Format("Eq_ImportErrorSchema", "Unsupported schema version. {0}", text);
		}
		if (Contains(text, "bandCount") || Contains(text, "frequenciesHz"))
		{
			return localizationManager.Format("Eq_ImportErrorBands", "EQ band layout does not match. {0}", text);
		}
		return localizationManager.Format("Eq_ImportFailed", "Import failed: {0}", text);
	}

	private static bool Contains(string haystack, string needle)
	{
		return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
	}

	private static string SanitizeDetail(string reason)
	{
		string text = new string(reason.Where((char c) => !char.IsControl(c)).ToArray()).Trim();
		if (text.Length > 280)
		{
			text = text.Substring(0, 277) + "...";
		}
		return text;
	}

	private static string StripPrefix(string text, string prefix)
	{
		if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return text.Substring(prefix.Length).Trim();
		}
		return text.Trim();
	}
}
