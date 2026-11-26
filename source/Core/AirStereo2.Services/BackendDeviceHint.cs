using System.Collections.Generic;

namespace AirStereo2.Services;

public sealed record BackendDeviceHint(string InstanceName, string[] Addresses, int Port, bool IsRaop, Dictionary<string, string> TxtRecords);
