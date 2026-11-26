using System;
using System.Collections.Generic;
using System.Linq;

namespace AirStereo2.Services;

internal sealed class ConcurrentStringSet
{
	private readonly object _gate = new object();

	private readonly HashSet<string> _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _set.Count;
			}
		}
	}

	public bool Add(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		lock (_gate)
		{
			return _set.Add(value.Trim());
		}
	}

	public bool Contains(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		lock (_gate)
		{
			return _set.Contains(value.Trim());
		}
	}

	public string[] Snapshot()
	{
		lock (_gate)
		{
			return _set.ToArray();
		}
	}

	public void Reset(IEnumerable<string> values)
	{
		ArgumentNullException.ThrowIfNull(values, "values");
		string[] array = (from value in values
			where !string.IsNullOrWhiteSpace(value)
			select value.Trim()).ToArray();
		lock (_gate)
		{
			_set.Clear();
			string[] array2 = array;
			foreach (string item in array2)
			{
				_set.Add(item);
			}
		}
	}
}
