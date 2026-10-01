using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AirStereo.Ui
{
    internal sealed class UpdateResult
    {
        internal string Version { get; set; }
        internal bool Available { get; set; }
        internal bool NoRelease { get; set; }
        internal string Page { get; set; }
    }

    internal static class UpdateService
    {
        internal static Version StableVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("版本号缺失");
            text = text.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            string[] parts = text.Split('.');
            if (parts.Length != 3) throw new FormatException("不支持的正式版本号");
            int[] values = new int[3];
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                    throw new FormatException("不支持的正式版本号");
            return new Version(values[0], values[1], values[2]);
        }

        internal static UpdateResult Parse(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                    throw new FormatException("更新来源不是正式发布");
                Version latest = StableVersion(root.GetProperty("tag_name").GetString());
                string page = root.GetProperty("html_url").GetString();
                if (!Uri.TryCreate(page, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
                    !uri.AbsolutePath.StartsWith("/Flourishze/AirStereo/releases/tag/", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("更新链接不属于指定仓库");
                return new UpdateResult { Version = latest.ToString(3), Available = latest > StableVersion(VersionInfo.Current), Page = page };
            }
        }

        internal static async Task<UpdateResult> CheckAsync(HttpMessageHandler handler = null, TimeSpan? timeout = null)
        {
            using (HttpClient client = handler == null ? new HttpClient() : new HttpClient(handler))
            {
                client.Timeout = timeout ?? TimeSpan.FromSeconds(12);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AirStereo/" + VersionInfo.Current);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using (HttpResponseMessage response = await client.GetAsync(
                    "https://api.github.com/repos/Flourishze/AirStereo/releases/latest").ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return new UpdateResult { NoRelease = true };
                    response.EnsureSuccessStatusCode();
                    return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
    }
}
