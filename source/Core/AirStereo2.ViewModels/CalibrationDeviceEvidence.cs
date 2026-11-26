using AirStereo2.Models;

namespace AirStereo2.ViewModels;

public sealed record CalibrationDeviceEvidence(CalibrationDeviceIdentity Identity, string Fingerprint = "", int? SelfReportedMs = null);
