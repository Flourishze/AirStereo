using System;

namespace AirStereo2.Services;

public sealed record DiagnosticsUploadResult(string ReportId, DateTimeOffset RetainedUntil);
