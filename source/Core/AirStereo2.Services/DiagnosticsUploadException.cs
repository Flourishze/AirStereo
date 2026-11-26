using System;

namespace AirStereo2.Services;

public sealed class DiagnosticsUploadException : Exception
{
	public DiagnosticsUploadException(string message)
		: base(message)
	{
	}

	public DiagnosticsUploadException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
