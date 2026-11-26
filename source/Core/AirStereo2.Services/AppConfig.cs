using System.Collections.Generic;
using System.Linq;
using AirStereo2.Models;

namespace AirStereo2.Services;

public class AppConfig
{
	public const int EqBandCount = 10;

	public const double EqMinGainDb = -12.0;

	public const double EqMaxGainDb = 12.0;

	public int MasterVolume { get; set; } = 50;

	public int StreamingModeIndex { get; set; }

	public int CustomBufferSize { get; set; } = 500;

	public bool MuteLocalOutputWhileStreaming { get; set; } = true;

	public int AcousticOffsetMs { get; set; }

	public int? ArchivedAcousticOffsetMs { get; set; }

	public bool AcousticOffsetDeactivated { get; set; }

	public bool ReceiverMediaControlEnabled { get; set; } = true;

	public bool SendNowPlayingMetadata { get; set; } = true;

	public bool AutomaticReceiverRecoveryEnabled { get; set; } = true;

	public string AudioCodec { get; set; } = "ALAC";

	public double? MiniPanelLeft { get; set; }

	public double? MiniPanelTop { get; set; }

	public double? MiniPanelWidth { get; set; }

	public double? MiniPanelHeight { get; set; }

	public bool MiniPanelTopmost { get; set; } = true;

	public int DefaultWindowModeIndex { get; set; }

	public int DockPlacementIndex { get; set; }

	public double? DeviceDockLeft { get; set; }

	public double? DeviceDockTop { get; set; }

	public bool SilentStartEnabled { get; set; } = true;

	public bool AutoConnectOnLaunch { get; set; } = true;

	public bool DeveloperModeEnabled { get; set; }

	public int? DevSoloLatencyMs { get; set; }

	public bool DevSoloLatencyOverrideEnabled { get; set; }

	public int? DevGroupLatencyMs { get; set; }

	public bool DevGroupLatencyOverrideEnabled { get; set; }

	public int? DevMixedLatencyMs { get; set; }

	public bool DevMixedLatencyOverrideEnabled { get; set; }

	public bool AirportPcmWarningShown { get; set; }

	public string? DiagnosticsInstallationId { get; set; }

	public int OnboardingCompletedVersion { get; set; }

	public List<LatencyCalibrationProfile> LatencyCalibrationProfiles { get; set; } = new List<LatencyCalibrationProfile>();

	public Dictionary<string, DeviceConfig> Devices { get; set; } = new Dictionary<string, DeviceConfig>();

	public int CaptureSourceIndex { get; set; }

	public string? SelectedCaptureDeviceId { get; set; }

	public int SelectedProcessId { get; set; }

	public string? SelectedProcessName { get; set; }

	public string? SelectedProcessExecutablePath { get; set; }

	public bool SilenceDetectionEnabled { get; set; }

	public int SilenceTimeoutMinutes { get; set; } = 120;

	public double SilenceThresholdDb { get; set; } = -60.0;

	public bool EqEnabled { get; set; }

	public List<double> EqBandGains { get; set; } = CreateDefaultEqBandGains();

	public double EqPreampDb { get; set; }

	public string EqPresetId { get; set; } = "builtin:flat";

	public List<EqUserPresetConfig> EqUserPresets { get; set; } = new List<EqUserPresetConfig>();

	public List<DefaultOutputDeviceRecord> DefaultOutputDevices { get; set; } = new List<DefaultOutputDeviceRecord>();

	public List<SenderGroupRecord> SenderGroups { get; set; } = new List<SenderGroupRecord>();

	public List<string> DismissedAppleGroupKeys { get; set; } = new List<string>();

	public Dictionary<string, string> AccessPasswords { get; set; } = new Dictionary<string, string>();

	public static List<double> CreateDefaultEqBandGains()
	{
		return EqLayout.NormalizeGains(null).ToList();
	}
}
