using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AirStereo2.Services;

public sealed class DiagnosticsUploadService : IDisposable
{
	private sealed record UploadPayload([property: JsonPropertyName("schema_version")] int SchemaVersion, [property: JsonPropertyName("installation_id")] string InstallationId, [property: JsonPropertyName("app_version")] string AppVersion, [property: JsonPropertyName("generated_utc")] DateTimeOffset GeneratedUtc, [property: JsonPropertyName("locale")] string Locale, [property: JsonPropertyName("user_description")] string UserDescription, [property: JsonPropertyName("contact"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contact, [property: JsonPropertyName("report")] string Report);

	private sealed record UploadResponse([property: JsonPropertyName("report_id")] string ReportId, [property: JsonPropertyName("retained_until")] DateTimeOffset RetainedUntil);

	private sealed record ErrorResponse([property: JsonPropertyName("error")] string Error);

	public const int SchemaVersion = 2;

	private const int MaxResponseBytes = 65536;

	private readonly HttpClient _httpClient;

	private readonly Uri? _endpoint;

	private bool _disposed;

	public bool IsConfigured => _endpoint != null;

	public DiagnosticsUploadService()
	{
		_endpoint = TryResolveEndpoint();
		_httpClient = new HttpClient(new HttpClientHandler
		{
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.None,
			UseCookies = false
		})
		{
			Timeout = TimeSpan.FromSeconds(25.0)
		};
		_httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirStereo2", GetAppVersion()));
	}

	public async Task<DiagnosticsUploadResult> UploadAsync(string installationId, string appVersion, string locale, string report, string userDescription, string? contact = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_endpoint == null)
		{
			throw new DiagnosticsUploadException("Online diagnostics upload is disabled in this open-source build. Please use local diagnostics export instead.");
		}
		if (!IsValidInstallationId(installationId))
		{
			throw new ArgumentException("The diagnostics installation identifier is invalid.", "installationId");
		}
		if (string.IsNullOrWhiteSpace(appVersion))
		{
			throw new ArgumentException("The app version is required.", "appVersion");
		}
		if (string.IsNullOrWhiteSpace(locale))
		{
			throw new ArgumentException("The locale is required.", "locale");
		}
		if (string.IsNullOrEmpty(report))
		{
			throw new ArgumentException("The diagnostics report is empty.", "report");
		}
		if (Encoding.UTF8.GetByteCount(report) > 4194304)
		{
			throw new ArgumentException("The diagnostics report exceeds the upload limit.", "report");
		}
		if (!ProblemReportText.IsDescriptionAcceptable(userDescription))
		{
			throw new ArgumentException("A problem description is required.", "userDescription");
		}
		if (userDescription.Length > 4000)
		{
			throw new ArgumentException("The problem description is too long.", "userDescription");
		}
		contact = (string.IsNullOrWhiteSpace(contact) ? null : contact.Trim());
		if (contact != null && contact.Length > 200)
		{
			throw new ArgumentException("The contact field is too long.", "contact");
		}
		UploadPayload inputValue = new UploadPayload(2, installationId, appVersion, DateTimeOffset.UtcNow, locale, userDescription, contact, report);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
			Content = JsonContent.Create(inputValue)
		};
		_ = 1;
		try
		{
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			byte[] array = await ReadBoundedResponseAsync(response, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!response.IsSuccessStatusCode)
			{
				string text = TryReadErrorCode(array) ?? $"HTTP {response.StatusCode}";
				throw new DiagnosticsUploadException("The diagnostics service rejected the report (" + text + ").");
			}
			UploadResponse uploadResponse;
			try
			{
				uploadResponse = JsonSerializer.Deserialize<UploadResponse>(array);
			}
			catch (JsonException innerException)
			{
				throw new DiagnosticsUploadException("The diagnostics service returned an invalid response.", innerException);
			}
			if (uploadResponse == null || !IsValidInstallationId(uploadResponse.ReportId) || uploadResponse.RetainedUntil <= DateTimeOffset.UtcNow || uploadResponse.RetainedUntil > DateTimeOffset.UtcNow.AddDays(91.0))
			{
				throw new DiagnosticsUploadException("The diagnostics service returned an incomplete response.");
			}
			return new DiagnosticsUploadResult(uploadResponse.ReportId, uploadResponse.RetainedUntil);
		}
		catch (DiagnosticsUploadException)
		{
			throw;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new DiagnosticsUploadException("The diagnostics service did not respond in time.");
		}
		catch (HttpRequestException innerException2)
		{
			throw new DiagnosticsUploadException("The diagnostics service could not be reached.", innerException2);
		}
		catch (IOException innerException3)
		{
			throw new DiagnosticsUploadException("The diagnostics service response could not be read.", innerException3);
		}
	}

	public static bool IsValidInstallationId(string? value)
	{
		if (!Guid.TryParseExact(value, "D", out var result))
		{
			return false;
		}
		string text = result.ToString("D");
		bool flag = string.Equals(value, text, StringComparison.Ordinal) && text[14] == '4';
		if (flag)
		{
			bool flag2;
			switch (text[19])
			{
			case '8':
			case '9':
			case 'a':
			case 'b':
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	public static bool TryValidateEndpoint(string? value, out Uri? endpoint)
	{
		endpoint = null;
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri result) || !string.Equals(result.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(result.UserInfo) || !string.IsNullOrEmpty(result.Query) || !string.IsNullOrEmpty(result.Fragment) || !string.Equals(result.AbsolutePath.TrimEnd('/'), "/v1/reports", StringComparison.Ordinal))
		{
			return false;
		}
		endpoint = result;
		return true;
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			_httpClient.Dispose();
		}
	}

	private static Uri? TryResolveEndpoint()
	{
		if (!TryValidateEndpoint(Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault((AssemblyMetadataAttribute attribute) => string.Equals(attribute.Key, "DiagnosticsUploadEndpoint", StringComparison.Ordinal))?.Value, out Uri? endpoint) || endpoint == null)
		{
			return null;
		}
		return endpoint;
	}

	private static string GetAppVersion()
	{
		return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
	}

	private static async Task<byte[]> ReadBoundedResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.Content.Headers.ContentLength > 65536)
		{
			throw new DiagnosticsUploadException("The diagnostics service response was unexpectedly large.");
		}
		byte[] result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
		{
			using MemoryStream buffer = new MemoryStream();
			byte[] chunk = new byte[4096];
			while (true)
			{
				int num = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (num == 0)
				{
					break;
				}
				if (buffer.Length + num > 65536)
				{
					throw new DiagnosticsUploadException("The diagnostics service response was unexpectedly large.");
				}
				buffer.Write(chunk, 0, num);
			}
			result = buffer.ToArray();
		}
		return result;
	}

	private static string? TryReadErrorCode(byte[] responseBytes)
	{
		try
		{
			ErrorResponse errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseBytes);
			return (string.IsNullOrWhiteSpace(errorResponse?.Error) || errorResponse.Error.Length > 64) ? null : errorResponse.Error;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
