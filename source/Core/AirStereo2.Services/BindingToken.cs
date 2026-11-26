using System;
using System.Runtime.CompilerServices;

namespace AirStereo2.Services;

public sealed record BindingToken(Guid BindingId, Guid TransportInstanceId)
{
	public bool IsEmpty
	{
		get
		{
			if (!(BindingId == Guid.Empty))
			{
				return TransportInstanceId == Guid.Empty;
			}
			return true;
		}
	}

	public static readonly BindingToken Empty = new BindingToken(Guid.Empty, Guid.Empty);

	[CompilerGenerated]
	private BindingToken(BindingToken original)
	{
		BindingId = original.BindingId;
		TransportInstanceId = original.TransportInstanceId;
	}
}
