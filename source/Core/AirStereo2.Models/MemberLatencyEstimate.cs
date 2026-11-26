namespace AirStereo2.Models;

public sealed record MemberLatencyEstimate(string DeviceName, string Model, string Ip, double TDevValue, double DPath, double JNet, double Sigma, double LrecMember);
