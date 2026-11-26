namespace AirStereo2.Models;

public sealed record NetworkStats(double DPath, double JNet, double RttP95, double Sigma, bool RttDerived, bool HasMeasuredEvidence = true);
