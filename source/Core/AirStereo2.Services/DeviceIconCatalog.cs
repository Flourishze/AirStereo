using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using AirStereo2.Models;

namespace AirStereo2.Services;

public static class DeviceIconCatalog
{
	private const string FallbackSpeakerPath = "M12 2a3 3 0 0 1 3 3v14a3 3 0 0 1-3 3H8a3 3 0 0 1-3-3V5a3 3 0 0 1 3-3h4zm0 2H8a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h4a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1zm-2 10a2 2 0 1 1 0 4 2 2 0 0 1 0-4zm0 2a.5.5 0 1 0 0 1 .5.5 0 0 0 0-1zm0-8a1 1 0 1 1 0 2 1 1 0 0 1 0-2z";

	private static readonly IReadOnlyDictionary<DeviceIconKind, string> IconFiles = new Dictionary<DeviceIconKind, string>
	{
		[DeviceIconKind.HifiSpeaker] = "hifispeaker.fill.svg",
		[DeviceIconKind.HifiSpeakerPair] = "hifispeaker.2.fill.svg",
		[DeviceIconKind.AppleTv] = "appletv.fill.svg",
		[DeviceIconKind.HomePod] = "homepod.fill.svg",
		[DeviceIconKind.HomePodPair] = "homepod.2.fill.svg",
		[DeviceIconKind.HomePodMini] = "homepod.mini.fill.svg",
		[DeviceIconKind.HomePodMiniPair] = "homepod.mini.2.fill.svg",
		[DeviceIconKind.HifiSpeakerAndAppleTv] = "hifispeaker.and.appletv.fill.svg",
		[DeviceIconKind.HifiSpeakerAndHomePod] = "hifispeaker.and.homepod.fill.svg",
		[DeviceIconKind.HifiSpeakerAndHomePodMini] = "hifispeaker.and.homepod.mini.fill.svg",
		[DeviceIconKind.HomePodAndAppleTv] = "homepod.and.appletv.fill.svg",
		[DeviceIconKind.HomePodAndAppleTvOutline] = "homepod.and.appletv.svg",
		[DeviceIconKind.HomePodMiniAndAppleTv] = "homepod.mini.and.appletv.fill.svg",
		[DeviceIconKind.AirPortExpress] = "airport.express.svg",
		[DeviceIconKind.Beats] = "beats.solobuds.chargingcase.fill.svg",
		[DeviceIconKind.MacBook] = "macbook.gen2.svg",
		[DeviceIconKind.MacMini] = "macmini.fill.svg",
		[DeviceIconKind.MacStudio] = "macstudio.fill.svg",
		[DeviceIconKind.NetworkSlash] = "network.slash.svg",
		[DeviceIconKind.Network] = "network.svg",
		[DeviceIconKind.SpeakerSlash] = "speaker.slash.fill.svg",
		[DeviceIconKind.SpeakerWave] = "speaker.wave.3.fill.svg",
		[DeviceIconKind.Gift] = "gift.fill.svg"
	};

	private static readonly ConcurrentDictionary<DeviceIconKind, Geometry> Cache = new ConcurrentDictionary<DeviceIconKind, Geometry>();

	public static Geometry NetworkSlashGeometry => GetGeometry(DeviceIconKind.NetworkSlash);

	public static Geometry NetworkGeometry => GetGeometry(DeviceIconKind.Network);

	public static Geometry GetGeometry(DeviceIconKind kind)
	{
		return Cache.GetOrAdd(kind, LoadGeometry);
	}

	private static Geometry LoadGeometry(DeviceIconKind kind)
	{
		if (!IconFiles.TryGetValue(kind, out string value))
		{
			return CreateFallbackGeometry();
		}
		string text = FindIconFile(value);
		if (text == null)
		{
			return CreateFallbackGeometry();
		}
		try
		{
			return LoadSvgGeometry(text, ShouldSkipLowOpacityLayers(value));
		}
		catch
		{
			return CreateFallbackGeometry();
		}
	}

	private static string? FindIconFile(string fileName)
	{
		return new string[4]
		{
			Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icons", fileName),
			Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName),
			Path.Combine(Environment.CurrentDirectory, "Assets", "Icons", fileName),
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Icons", fileName)
		}.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
	}

	private static bool ShouldSkipLowOpacityLayers(string fileName)
	{
		return fileName.StartsWith("hifispeaker", StringComparison.OrdinalIgnoreCase);
	}

	private static Geometry LoadSvgGeometry(string path, bool skipLowOpacityLayers)
	{
		XmlReaderSettings settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Ignore,
			XmlResolver = null
		};
		using XmlReader reader = XmlReader.Create(path, settings);
		string[] array = (from element in XDocument.Load(reader).Descendants()
			where element.Name.LocalName == "path"
			where ShouldUsePath(element, skipLowOpacityLayers)
			select (string?)element.Attribute("d") into data
			where !string.IsNullOrWhiteSpace(data)
			select data).ToArray();
		if (array.Length == 0)
		{
			return CreateFallbackGeometry();
		}
		GeometryGroup geometryGroup = new GeometryGroup
		{
			FillRule = FillRule.Nonzero
		};
		string[] array2 = array;
		foreach (string source in array2)
		{
			geometryGroup.Children.Add(Geometry.Parse(source));
		}
		((Freezable)geometryGroup).Freeze();
		return geometryGroup;
	}

	private static bool ShouldUsePath(XElement element, bool skipLowOpacityLayers)
	{
		if (!skipLowOpacityLayers)
		{
			return true;
		}
		string text = (string?)element.Attribute("fill-opacity");
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}
		if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return result >= 0.5;
		}
		return true;
	}

	private static Geometry CreateFallbackGeometry()
	{
		Geometry geometry = Geometry.Parse("M12 2a3 3 0 0 1 3 3v14a3 3 0 0 1-3 3H8a3 3 0 0 1-3-3V5a3 3 0 0 1 3-3h4zm0 2H8a1 1 0 0 0-1 1v14a1 1 0 0 0 1 1h4a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1zm-2 10a2 2 0 1 1 0 4 2 2 0 0 1 0-4zm0 2a.5.5 0 1 0 0 1 .5.5 0 0 0 0-1zm0-8a1 1 0 1 1 0 2 1 1 0 0 1 0-2z");
		((Freezable)geometry).Freeze();
		return geometry;
	}
}
