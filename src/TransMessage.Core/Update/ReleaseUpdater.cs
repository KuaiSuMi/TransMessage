using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Update;

public sealed record ReleaseInfo(string Version, string PageUrl, string AssetUrl, string ChecksumsUrl, bool Newer);
public sealed record UpdateCache(DateTimeOffset CheckedAt, ReleaseInfo? Release, string? Error);

public sealed class ReleaseUpdater(HttpClient http, AppPaths paths)
{
    public const string Repository = "KuaiSuMi/TransMessage";
    private string CachePath => Path.Combine(paths.Root, "update-check.json");
    public UpdateCache? Cached()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(CachePath)) : null; }
        catch (JsonException) { return null; }
    }
    public async Task<UpdateCache> CheckAsync(string currentVersion, bool force = false, CancellationToken ct = default)
    {
        var cached = Cached();
        if (!force && cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromHours(24)) return cached;
        UpdateCache result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("TransMessage/0.1.0"); request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) result = new(DateTimeOffset.UtcNow, null, "尚未发布公开版本。");
            else
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                result = new(DateTimeOffset.UtcNow, Parse(json.RootElement, currentVersion), null);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or AppException or OperationCanceledException)
        { result = new(DateTimeOffset.UtcNow, cached?.Release, AppException.Describe(e)); }
        AtomicFile.Write(CachePath, JsonSerializer.Serialize(result));
        return result;
    }
    public static ReleaseInfo Parse(JsonElement release, string currentVersion)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || !Version.TryParse(currentVersion.Split('+')[0], out var current))
            throw new AppException("UPDATE_VERSION", "版本号格式无效，无法安全更新。");
        string? asset = null, sums = null;
        foreach (var entry in release.GetProperty("assets").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (name == "TransMessage-win-x64.zip") asset = entry.GetProperty("browser_download_url").GetString();
            if (name == "SHA256SUMS") sums = entry.GetProperty("browser_download_url").GetString();
        }
        if (!TrustedAsset(asset) || !TrustedAsset(sums)) throw new AppException("UPDATE_ASSETS", "Release 缺少有效的 Windows 压缩包或校验文件。");
        return new(tag.TrimStart('v'), $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}", asset!, sums!, version > current);
    }
    private static bool TrustedAsset(string? url) => url is not null && url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal);
    public static string ExpectedHash(string checksums)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var pieces = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 2 && pieces[1].TrimStart('*') == "TransMessage-win-x64.zip" && pieces[0].Length == 64 && pieces[0].All(Uri.IsHexDigit)) return pieces[0];
        }
        throw new AppException("UPDATE_HASH_MISSING", "SHA256SUMS 中没有当前安装包的有效校验值。");
    }
    public static async Task VerifyAsync(string archive, string expected, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(archive);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new AppException("UPDATE_HASH", "下载校验失败，当前版本未被修改。");
    }
    public async Task<string> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (!TrustedAsset(release.AssetUrl) || !TrustedAsset(release.ChecksumsUrl)) throw new AppException("UPDATE_SOURCE", "更新来源不受信任。");
        var stage = Path.Combine(paths.Root, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var archive = Path.Combine(stage, "download.zip.partial");
        try
        {
            var sums = await DownloadSmallAsync(release.ChecksumsUrl, ct);
            var expected = ExpectedHash(sums);
            using var response = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 400 * 1024 * 1024) throw new AppException("UPDATE_SIZE", "更新包过大。");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = File.Create(archive))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > 400 * 1024 * 1024) throw new AppException("UPDATE_SIZE", "更新包超过大小限制。");
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await VerifyAsync(archive, expected, ct);
            var output = Path.Combine(stage, "TransMessage.exe");
            using (var zip = ZipFile.OpenRead(archive))
            {
                var executable = zip.Entries.SingleOrDefault(e => e.FullName == "TransMessage.exe");
                if (executable is null || executable.Length > 500 * 1024 * 1024) throw new AppException("UPDATE_LAYOUT", "更新包结构无效。");
                executable.ExtractToFile(output);
            }
            File.Delete(archive); return output;
        }
        catch { if (File.Exists(archive)) File.Delete(archive); throw; }
    }
    private async Task<string> DownloadSmallAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[65537]; var total = 0;
        while (total < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(total), ct); if (count == 0) break; total += count;
        }
        if (total > 65536) throw new AppException("UPDATE_HASH_SIZE", "校验文件大小无效。");
        return System.Text.Encoding.UTF8.GetString(bytes, 0, total);
    }
}
