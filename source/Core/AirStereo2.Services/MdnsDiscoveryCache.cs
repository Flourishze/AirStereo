using System;
using System.Collections.Generic;
using System.Linq;
using Makaretu.Dns;

namespace AirStereo2.Services;

internal sealed class MdnsDiscoveryCache
{
	internal sealed record ServiceRecord(string FullName, SRVRecord Srv, TXTRecord Txt, string[] Addresses);

	private sealed record CachedRecords(ResourceRecord[] Records, DateTimeOffset Observed, DateTimeOffset Expires);

	internal sealed record TxtEvidence(TXTRecord Txt, DateTimeOffset Observed, DateTimeOffset Expires);

	internal sealed record Result(IReadOnlyList<ServiceRecord> Services, IReadOnlyList<Question> Queries);

	private readonly object _lock = new object();

	private readonly Dictionary<string, CachedRecords> _records = new Dictionary<string, CachedRecords>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, DateTimeOffset> _queries = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

	internal Result Apply(Message message, DateTimeOffset now, string queryScope = "multicast")
	{
		ResourceRecord[] incoming;
		List<Question> queries;
		lock (_lock)
		{
			string[] array = (from pair in _records
				where pair.Value.Expires <= now
				select pair.Key).ToArray();
			foreach (string key in array)
			{
				_records.Remove(key);
			}
			array = (from pair in _queries
				where pair.Value <= now
				select pair.Key).ToArray();
			foreach (string key2 in array)
			{
				_queries.Remove(key2);
			}
			incoming = (from record in message.Answers.Concat(message.AdditionalRecords)
				where (record is ARecord || record is SRVRecord || record is TXTRecord || record is PTRRecord) ? true : false
				select record).ToArray();
			foreach (IGrouping<string, ResourceRecord> item in from record in incoming
				where !(record is PTRRecord)
				group record by Key(((object)record.Name).ToString(), record.Type))
			{
				if (item.First() is ARecord)
				{
					foreach (ARecord item2 in item.Cast<ARecord>())
					{
						string key3 = $"{item.Key}/{((AddressRecord)item2).Address}";
						if (((ResourceRecord)item2).TTL <= TimeSpan.Zero)
						{
							_records.Remove(key3);
							continue;
						}
						Dictionary<string, CachedRecords> records = _records;
						ResourceRecord[] records2 = (ResourceRecord[])(object)new ARecord[1] { item2 };
						records[key3] = new CachedRecords(records2, now, now + ((ResourceRecord)item2).TTL);
					}
					continue;
				}
				ResourceRecord[] array2 = item.Where((ResourceRecord record) => record.TTL > TimeSpan.Zero).ToArray();
				if (array2.Length == 0)
				{
					_records.Remove(item.Key);
					continue;
				}
				ResourceRecord[] array3 = (ResourceRecord[])(object)new ResourceRecord[1] { array2[^1] };
				_records[item.Key] = new CachedRecords(array3, now, now + array3.Min((ResourceRecord record) => record.TTL));
			}
			foreach (PTRRecord item3 in from record in incoming.OfType<PTRRecord>()
				where ((ResourceRecord)record).TTL <= TimeSpan.Zero
				select record)
			{
				_records.Remove(Key(((object)item3.DomainName).ToString(), (DnsType)33));
				_records.Remove(Key(((object)item3.DomainName).ToString(), (DnsType)16));
			}
			string[] array4 = (from record in _records.Values.SelectMany((CachedRecords set) => set.Records)
				where (record is SRVRecord || record is TXTRecord) ? true : false
				select ((object)record.Name).ToString()).Concat(from record in incoming.OfType<PTRRecord>()
				where ((ResourceRecord)record).TTL > TimeSpan.Zero
				select ((object)record.DomainName).ToString()).Where(IsReceiverInstance).Distinct<string>(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			List<ServiceRecord> list = new List<ServiceRecord>();
			queries = new List<Question>();
			array = array4;
			foreach (string text in array)
			{
				SRVRecord val = Lookup<SRVRecord>(text, (DnsType)33, (DateTimeOffset?)now).FirstOrDefault();
				TXTRecord val2 = Lookup<TXTRecord>(text, (DnsType)16, (DateTimeOffset?)now).FirstOrDefault();
				if ((ResourceRecord)(object)val == (ResourceRecord)null)
				{
					Request(text, (DnsType)33);
				}
				if ((ResourceRecord)(object)val2 == (ResourceRecord)null)
				{
					Request(text, (DnsType)16);
				}
				string[] array5 = (((ResourceRecord)(object)val == (ResourceRecord)null) ? Array.Empty<string>() : (from record in Lookup<ARecord>(((object)val.Target).ToString(), (DnsType)1, (DateTimeOffset?)now)
					select ((AddressRecord)record).Address.ToString()).Distinct().ToArray());
				if ((ResourceRecord)(object)val != (ResourceRecord)null && array5.Length == 0)
				{
					Request(((object)val.Target).ToString(), (DnsType)1);
				}
				if ((ResourceRecord)(object)val != (ResourceRecord)null && (ResourceRecord)(object)val2 != (ResourceRecord)null && array5.Length != 0)
				{
					list.Add(new ServiceRecord(text, val, val2, array5));
				}
			}
			return new Result(list, queries);
		}
		void Request(string name, DnsType type)
		{
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0102: Unknown result type (might be due to invalid IL or missing references)
			//IL_0107: Unknown result type (might be due to invalid IL or missing references)
			//IL_010e: Unknown result type (might be due to invalid IL or missing references)
			//IL_010f: Unknown result type (might be due to invalid IL or missing references)
			//IL_011a: Expected O, but got Unknown
			string key4 = queryScope + ":" + Key(name, type);
			if (!_queries.ContainsKey(key4))
			{
				_queries[key4] = now.AddSeconds(5.0);
				DomainName name2 = incoming.SelectMany(delegate(ResourceRecord record)
				{
					PTRRecord val3 = (PTRRecord)(object)((record is PTRRecord) ? record : null);
					return (IEnumerable<DomainName>)(object)((val3 != null) ? new DomainName[2]
					{
						((ResourceRecord)val3).Name,
						val3.DomainName
					} : new DomainName[1] { record.Name });
				}).Concat(_records.Values.SelectMany((CachedRecords set) => set.Records).SelectMany(delegate(ResourceRecord record)
				{
					SRVRecord val3 = (SRVRecord)(object)((record is SRVRecord) ? record : null);
					return (IEnumerable<DomainName>)(object)((val3 != null) ? new DomainName[2]
					{
						((ResourceRecord)val3).Name,
						val3.Target
					} : new DomainName[1] { record.Name });
				})).First((DomainName candidate) => string.Equals(((object)candidate).ToString().TrimEnd('.'), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
				queries.Add(new Question
				{
					Name = name2,
					Type = type
				});
			}
		}
	}

	internal TxtEvidence? AirPlayEvidence(string instanceName, string publicKey = "")
	{
		lock (_lock)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			IEnumerable<TxtEvidence> source = from evidence in _records.Values.Where((CachedRecords set) => set.Expires > now).SelectMany((CachedRecords set) => from txt in set.Records.OfType<TXTRecord>()
					select new TxtEvidence(txt, set.Observed, set.Expires))
				where ((object)((ResourceRecord)evidence.Txt).Name).ToString().TrimEnd('.').EndsWith("._airplay._tcp.local", StringComparison.OrdinalIgnoreCase)
				select evidence;
			if (!string.IsNullOrWhiteSpace(instanceName))
			{
				return source.FirstOrDefault((TxtEvidence evidence) => string.Equals(((object)((ResourceRecord)evidence.Txt).Name).ToString().TrimEnd('.'), instanceName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
			}
			if (string.IsNullOrWhiteSpace(publicKey))
			{
				return null;
			}
			TxtEvidence[] array = source.Where((TxtEvidence evidence) => evidence.Txt.Strings.Any((string entry) => string.Equals(entry, "pk=" + publicKey, StringComparison.OrdinalIgnoreCase))).ToArray();
			return (array.Length == 1) ? array[0] : null;
		}
	}

	internal TxtEvidence? AirPlayCompanionEvidence(string raopInstance, string publicKey)
	{
		lock (_lock)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			TxtEvidence[] source = (from evidence in _records.Values.Where((CachedRecords set) => set.Expires > now).SelectMany((CachedRecords set) => from txt in set.Records.OfType<TXTRecord>()
					select new TxtEvidence(txt, set.Observed, set.Expires))
				where ((object)((ResourceRecord)evidence.Txt).Name).ToString().TrimEnd('.').EndsWith("._airplay._tcp.local", StringComparison.OrdinalIgnoreCase)
				select evidence).ToArray();
			string deviceId = AirPlayDeviceIdentity.ExtractRaopDeviceId(raopInstance);
			if (!string.IsNullOrWhiteSpace(publicKey))
			{
				TxtEvidence[] array = source.Where((TxtEvidence evidence) => string.Equals(Field(evidence.Txt, "pk"), publicKey, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (array.Length != 0)
				{
					return (array.Length == 1) ? array[0] : null;
				}
			}
			if (string.IsNullOrEmpty(deviceId))
			{
				return null;
			}
			TxtEvidence[] array2 = source.Where((TxtEvidence evidence) => string.Equals(deviceId, AirPlayDeviceIdentity.NormalizeDeviceId(Field(evidence.Txt, "deviceid")), StringComparison.OrdinalIgnoreCase)).ToArray();
			return (array2.Length == 1 && AirPlayDeviceIdentity.MatchesReceiverAdvertisement(deviceId, publicKey, Field(array2[0].Txt, "deviceid"), Field(array2[0].Txt, "pk"))) ? array2[0] : null;
		}
		static string Field(TXTRecord txt, string key)
		{
			string[] array3 = (from entry in txt.Strings
				where entry.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
				select entry.Substring(key.Length + 1)).ToArray();
			if (array3.Length != 1)
			{
				return string.Empty;
			}
			return array3[0];
		}
	}

	internal IReadOnlyList<Question> RefreshQueries(DateTimeOffset now, string queryScope)
	{
		List<Question> questions;
		lock (_lock)
		{
			IEnumerable<string> enumerable = (from record in _records.Values.Where((CachedRecords set) => set.Expires > now).SelectMany((CachedRecords set) => set.Records)
				where (record is SRVRecord || record is TXTRecord) ? true : false
				select ((object)record.Name).ToString()).Where(IsReceiverInstance).Distinct<string>(StringComparer.OrdinalIgnoreCase);
			questions = new List<Question>();
			foreach (string item in enumerable)
			{
				Request(item, (DnsType)16);
				Request(item, (DnsType)33);
				foreach (SRVRecord item2 in Lookup<SRVRecord>(item, (DnsType)33, (DateTimeOffset?)now))
				{
					Request(((object)item2.Target).ToString(), (DnsType)1);
				}
			}
			return questions;
		}
		void Request(string name, DnsType type)
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0100: Expected O, but got Unknown
			string key = "refresh:" + queryScope + ":" + Key(name, type);
			if (!_queries.TryGetValue(key, out var value) || !(value > now))
			{
				_queries[key] = now.AddSeconds(20.0);
				DomainName name2 = _records.Values.SelectMany((CachedRecords set) => set.Records).SelectMany(delegate(ResourceRecord record)
				{
					SRVRecord val = (SRVRecord)(object)((record is SRVRecord) ? record : null);
					return (IEnumerable<DomainName>)(object)((val != null) ? new DomainName[2]
					{
						((ResourceRecord)val).Name,
						val.Target
					} : new DomainName[1] { record.Name });
				}).First((DomainName candidate) => string.Equals(((object)candidate).ToString().TrimEnd('.'), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
				questions.Add(new Question
				{
					Name = name2,
					Type = type
				});
			}
		}
	}

	internal IReadOnlyList<string> HomePodProbeAddresses()
	{
		lock (_lock)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			return (from record in (from record in _records.Values.Where((CachedRecords set) => set.Expires > now).SelectMany((CachedRecords set) => set.Records).OfType<TXTRecord>()
					where IsReceiverInstance(((object)((ResourceRecord)record).Name).ToString()) && record.Strings.Any((string entry) => entry.StartsWith("am=AudioAccessory", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("model=AudioAccessory", StringComparison.OrdinalIgnoreCase))
					select record).SelectMany((TXTRecord record) => Lookup<SRVRecord>(((object)((ResourceRecord)record).Name).ToString(), (DnsType)33, (DateTimeOffset?)null)).SelectMany((SRVRecord srv) => Lookup<ARecord>(((object)srv.Target).ToString(), (DnsType)1, (DateTimeOffset?)null))
				select ((AddressRecord)record).Address.ToString()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		}
	}

	internal string Diagnostics()
	{
		lock (_lock)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			string[] array = (from record in _records.Values.Where((CachedRecords set) => set.Expires > now).SelectMany((CachedRecords set) => set.Records).OfType<TXTRecord>()
				where IsReceiverInstance(((object)((ResourceRecord)record).Name).ToString())
				select record).OrderBy<TXTRecord, string>((TXTRecord record) => ((object)((ResourceRecord)record).Name).ToString(), StringComparer.OrdinalIgnoreCase).Select(delegate(TXTRecord record)
			{
				SRVRecord val = Lookup<SRVRecord>(((object)((ResourceRecord)record).Name).ToString(), (DnsType)33, (DateTimeOffset?)null).FirstOrDefault();
				int value = ((!((ResourceRecord)(object)val == (ResourceRecord)null)) ? Lookup<ARecord>(((object)val.Target).ToString(), (DnsType)1, (DateTimeOffset?)null).Count() : 0);
				return $"{((ResourceRecord)record).Name}: {string.Join("; ", record.Strings.Where(IsDiagnosticField))}; srv-present={(ResourceRecord)(object)val != (ResourceRecord)null}; ipv4-count={value}";
			}).ToArray();
			return (array.Length == 0) ? "(no receiver TXT records received)" : string.Join(Environment.NewLine, array);
		}
	}

	internal void Clear()
	{
		lock (_lock)
		{
			_records.Clear();
			_queries.Clear();
		}
	}

	private IEnumerable<T> Lookup<T>(string name, DnsType type, DateTimeOffset? now = null) where T : ResourceRecord
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		return from record in _records.Values.Where((CachedRecords set) => set.Expires > (now ?? DateTimeOffset.UtcNow)).SelectMany((CachedRecords set) => set.Records).OfType<T>()
			where ((ResourceRecord)record).Type == type && string.Equals(((object)((ResourceRecord)record).Name).ToString().TrimEnd('.'), name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
			select record;
	}

	private static string Key(string name, DnsType type)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		return $"{name.TrimEnd('.')}/{type}";
	}

	private static bool IsReceiverInstance(string name)
	{
		if (!name.TrimEnd('.').EndsWith("._airplay._tcp.local", StringComparison.OrdinalIgnoreCase))
		{
			return name.TrimEnd('.').EndsWith("._raop._tcp.local", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsDiagnosticField(string entry)
	{
		int num = entry.IndexOf('=');
		if (num > 0)
		{
			return new string[13]
			{
				"deviceid", "model", "am", "gid", "pgid", "tsid", "gpn", "igl", "gcgl", "pgcgl",
				"c", "flags", "sf"
			}.Contains<string>(entry.Substring(0, num), StringComparer.OrdinalIgnoreCase);
		}
		return false;
	}
}
