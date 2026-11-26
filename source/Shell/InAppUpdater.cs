using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirStereo2;

internal sealed record UpdateCheckResult(
    bool Ok,
    bool NoRelease,
    bool HasUpdate,
    string RemoteTag,
    string RemoteVersion,
    string ReleaseUrl,
    string Error);

internal readonly record struct UpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    double Percent,
    double BytesPerSecond,
    string Stage);

internal static class InAppUpdater
{
    internal const string CurrentAppVersion = "1.1.1";
    internal const string RepoHomeUrl = "https://github.com/Flourishze/AirStereo";
    internal const string IssuesUrl = "https://github.com/Flourishze/AirStereo/issues";
    internal const string ReleasesPageUrl = "https://github.com/Flourishze/AirStereo/releases";
    internal const string StoreProductUrl = "ms-windows-store://pdp/?productid=9NMX9H3GBGJ4";
    const string LatestReleaseWebUrl = "https://github.com/Flourishze/AirStereo/releases/latest";
    const string LatestReleaseApiUrl = "https://api.github.com/repos/Flourishze/AirStereo/releases/latest";

    static readonly byte[] MsiOleHeader = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    static readonly SemaphoreSlim quietCheckGate = new(1, 1);

    internal static bool IsStoreEdition => WinUiNative.IsPackaged();
    internal static UpdateCheckResult? LatestUpdate { get; private set; }
    internal static bool HasPendingUpdate =>
        !IsStoreEdition &&
        UiPreferences.Read("autoUpdateNotify", false) &&
        LatestUpdate is { Ok: true, HasUpdate: true };

    internal static event Action? StateChanged;

    internal static void NotifyPreferenceChanged()
    {
        if (!UiPreferences.Read("autoUpdateNotify", false)) {
            LatestUpdate = null;
        }
        StateChanged?.Invoke();
    }

    internal static HttpClient CreateHttpClient(bool useProxy, bool allowRedirect, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler {
            AllowAutoRedirect = allowRedirect,
            UseProxy = useProxy,
            Proxy = useProxy ? WebRequest.DefaultWebProxy : null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AirStereo2/{CurrentAppVersion}");
        return client;
    }

    internal static bool TryParseVersion(string text, out Version version)
    {
        string core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0].Trim();
        return Version.TryParse(core, out version!);
    }

    internal static async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default)
    {
        string lastError = "";
        // 1) Primary: GitHub web redirect (/releases/latest -> /releases/tag/<tag>), avoids 60/hr REST API rate limits.
        foreach (bool useProxy in new[] { true, false }) {
            try {
                using var http = CreateHttpClient(useProxy, allowRedirect: false, TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseWebUrl);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                int code = (int)resp.StatusCode;
                if (code is >= 300 and < 400 && resp.Headers.Location is { } loc) {
                    string target = loc.IsAbsoluteUri ? loc.AbsoluteUri : new Uri(new Uri(LatestReleaseWebUrl), loc).AbsoluteUri;
                    const string marker = "/releases/tag/";
                    int idx = target.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) {
                        string tag = Uri.UnescapeDataString(target[(idx + marker.Length)..].Split('?', '#')[0].Trim('/'));
                        if (!string.IsNullOrWhiteSpace(tag))
                            return FinalizeCheck(tag, target);
                    }
                    if (target.TrimEnd('/').EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
                        return new UpdateCheckResult(true, true, false, "", "", ReleasesPageUrl, "");
                } else if (resp.StatusCode == HttpStatusCode.NotFound) {
                    return new UpdateCheckResult(true, true, false, "", "", ReleasesPageUrl, "");
                }
                lastError = $"web(proxy={useProxy}) HTTP {code}";
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = $"web(proxy={useProxy}) {ex.Message}"; }
        }

        // 2) Fallback: GitHub REST API.
        foreach (bool useProxy in new[] { true, false }) {
            try {
                using var http = CreateHttpClient(useProxy, allowRedirect: true, TimeSpan.FromSeconds(15));
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await http.GetAsync(LatestReleaseApiUrl, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new UpdateCheckResult(true, true, false, "", "", ReleasesPageUrl, "");
                if (!resp.IsSuccessStatusCode) {
                    lastError = $"api(proxy={useProxy}) HTTP {(int)resp.StatusCode}";
                    continue;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "").Trim() : "";
                string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? "").Trim() : ReleasesPageUrl;
                if (!string.IsNullOrWhiteSpace(tag))
                    return FinalizeCheck(tag, htmlUrl);
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = $"api(proxy={useProxy}) {ex.Message}"; }
        }

        return new UpdateCheckResult(false, false, false, "", "", ReleasesPageUrl, lastError);
    }

    static UpdateCheckResult FinalizeCheck(string tag, string url)
    {
        string remoteVer = tag.Trim().TrimStart('v', 'V');
        bool hasUpdate = TryParseVersion(remoteVer, out var remote) &&
                         TryParseVersion(CurrentAppVersion, out var local) &&
                         remote > local;
        var res = new UpdateCheckResult(true, false, hasUpdate, tag, remoteVer, string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url, "");
        LatestUpdate = res;
        StateChanged?.Invoke();
        return res;
    }

    internal static async Task CheckQuietlyIfEnabledAsync(bool force = false)
    {
        if (IsStoreEdition || !UiPreferences.Read("autoUpdateNotify", false)) return;
        if (!await quietCheckGate.WaitAsync(0)) return;
        try {
            if (!force) {
                long lastTicks = UiPreferences.Read("lastAutoUpdateCheckUtcTicks", 0L);
                if (lastTicks > 0 && (DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc)) < TimeSpan.FromHours(12))
                    return;
            }
            UiPreferences.Set("lastAutoUpdateCheckUtcTicks", DateTime.UtcNow.Ticks);
            await CheckLatestAsync();
        } catch {
            // Silent check must never interrupt the user on network issues.
        } finally {
            quietCheckGate.Release();
        }
    }

    internal static async Task<(bool Ok, string MsiPath, string Error)> DownloadAndVerifyMsiAsync(
        string remoteTag,
        string remoteVersion,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (IsStoreEdition)
            return (false, "", WinUiLanguage.T("Microsoft Store 版本请通过微软商店更新。"));

        string cleanVer = remoteVersion.Trim().TrimStart('v', 'V');
        string cleanTag = string.IsNullOrWhiteSpace(remoteTag) ? "v" + cleanVer : remoteTag.Trim();
        string fileName = $"AirStereo2-{cleanVer}-x64.msi";
        string baseUrl = $"https://github.com/Flourishze/AirStereo/releases/download/{Uri.EscapeDataString(cleanTag)}";
        string sumsUrl = $"{baseUrl}/SHA256SUMS.txt";
        string msiUrl = $"{baseUrl}/{Uri.EscapeDataString(fileName)}";

        string updateDir = Path.Combine(Path.GetTempPath(), "AirStereo2-Update");
        Directory.CreateDirectory(updateDir);
        CleanupStaleFiles(updateDir, fileName);

        // 1) Download and parse SHA256SUMS.txt first (mandatory integrity check).
        progress?.Report(new UpdateDownloadProgress(0, 0, 0, 0, WinUiLanguage.T("正在获取校验文件…")));
        string? expectedHash = await FetchExpectedSha256Async(sumsUrl, fileName, ct);
        if (string.IsNullOrWhiteSpace(expectedHash))
            return (false, "", WinUiLanguage.T("未能获取或解析 SHA256SUMS.txt 校验值，已中止更新以保障安全。"));

        string finalPath = Path.Combine(updateDir, fileName);
        string partPath = finalPath + ".part";

        // Reuse already downloaded and verified MSI if present.
        if (File.Exists(finalPath) && await VerifyMsiFileAsync(finalPath, expectedHash, ct)) {
            long existingSize = new FileInfo(finalPath).Length;
            progress?.Report(new UpdateDownloadProgress(existingSize, existingSize, 100, 0, WinUiLanguage.T("校验通过，准备安装…")));
            return (true, finalPath, "");
        }

        // 2) Stream download MSI with proxy -> direct fallback.
        string lastError = "";
        bool downloaded = false;
        foreach (bool useProxy in new[] { true, false }) {
            ct.ThrowIfCancellationRequested();
            try { TryDelete(partPath); } catch { }
            try {
                using var http = CreateHttpClient(useProxy, allowRedirect: true, TimeSpan.FromMinutes(15));
                using var resp = await http.GetAsync(msiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) {
                    lastError = $"HTTP {(int)resp.StatusCode}";
                    continue;
                }
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                byte[] buffer = new byte[65536];
                long received = 0;
                var sw = Stopwatch.StartNew();
                long lastReportMs = -500;
                int read;
                while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0) {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastReportMs >= 150) {
                        lastReportMs = nowMs;
                        double speed = nowMs > 0 ? received * 1000.0 / nowMs : 0;
                        double pct = total > 0 ? Math.Min(99.0, received * 100.0 / total) : 0;
                        progress?.Report(new UpdateDownloadProgress(received, total, pct, speed, WinUiLanguage.T("正在下载安装包…")));
                    }
                }
                await fileStream.FlushAsync(ct);
                downloaded = received >= 10 * 1024 * 1024;
                if (!downloaded) {
                    lastError = WinUiLanguage.T("下载文件体积异常，可能未完整下载。");
                    continue;
                }
                break;
            } catch (OperationCanceledException) {
                TryDelete(partPath);
                throw;
            } catch (Exception ex) {
                TryDelete(partPath);
                lastError = ex.Message;
            }
        }

        if (!downloaded) {
            TryDelete(partPath);
            return (false, "", string.Format(WinUiLanguage.T("下载安装包失败：{0}"), lastError));
        }

        // 3) Verify OLE MSI header & SHA-256 hash before moving into place.
        progress?.Report(new UpdateDownloadProgress(0, 0, 99, 0, WinUiLanguage.T("正在校验 SHA-256 完整性…")));
        if (!await VerifyMsiFileAsync(partPath, expectedHash, ct)) {
            TryDelete(partPath);
            return (false, "", WinUiLanguage.T("安装包 SHA-256 校验未通过，文件可能已损坏，已自动删除。"));
        }

        File.Move(partPath, finalPath, overwrite: true);
        long finalLen = new FileInfo(finalPath).Length;
        progress?.Report(new UpdateDownloadProgress(finalLen, finalLen, 100, 0, WinUiLanguage.T("校验通过，准备安装…")));
        return (true, finalPath, "");
    }

    static async Task<string?> FetchExpectedSha256Async(string sumsUrl, string targetFileName, CancellationToken ct)
    {
        foreach (bool useProxy in new[] { true, false }) {
            try {
                using var http = CreateHttpClient(useProxy, allowRedirect: true, TimeSpan.FromSeconds(20));
                using var resp = await http.GetAsync(sumsUrl, ct);
                if (!resp.IsSuccessStatusCode) continue;
                string body = await resp.Content.ReadAsStringAsync(ct);
                foreach (string rawLine in body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    var m = Regex.Match(rawLine.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                    if (!m.Success) continue;
                    string hash = m.Groups[1].Value.ToLowerInvariant();
                    string name = m.Groups[2].Value.Trim();
                    if (string.Equals(name, targetFileName, StringComparison.OrdinalIgnoreCase))
                        return hash;
                }
            } catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    static async Task<bool> VerifyMsiFileAsync(string path, string expectedSha256, CancellationToken ct)
    {
        try {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 10 * 1024 * 1024) return false;
            byte[] header = new byte[8];
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true)) {
                int n = await fs.ReadAsync(header.AsMemory(0, 8), ct);
                if (n != 8) return false;
                for (int i = 0; i < 8; i++)
                    if (header[i] != MsiOleHeader[i]) return false;
                fs.Position = 0;
                byte[] digest = await SHA256.HashDataAsync(fs, ct);
                string actual = Convert.ToHexString(digest).ToLowerInvariant();
                return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        } catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    internal static void LaunchMsiUpgrade(string msiPath)
    {
        if (IsStoreEdition || !File.Exists(msiPath))
            throw new InvalidOperationException("安装包不可用");
        // Launch Windows Installer directly; MSI MajorUpgrade + CloseApplication gracefully shuts down
        // AirStereo2 after UAC approval, preserves the install folder, and offers auto-relaunch on finish.
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\"") { UseShellExecute = true });
    }

    internal static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    static void CleanupStaleFiles(string dir, string keepFileName)
    {
        try {
            foreach (string f in Directory.GetFiles(dir)) {
                string name = Path.GetFileName(f);
                if (!string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
            }
        } catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
