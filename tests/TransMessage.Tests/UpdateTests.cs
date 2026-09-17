using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Update;

namespace TransMessage.Tests;

public sealed class UpdateTests
{
    private static readonly string Prefix = "https://github.com/KuaiSuMi/TransMessage/releases/download/v0.2.0/";
    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.2.0", false)]
    [InlineData("0.3.0", false)]
    public void VersionsAndAssets(string current, bool newer)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = "v0.2.0", assets = new[] { new { name = "TransMessage-win-x64.zip", browser_download_url = Prefix + "TransMessage-win-x64.zip" }, new { name = "SHA256SUMS", browser_download_url = Prefix + "SHA256SUMS" } } }));
        Assert.Equal(newer, ReleaseUpdater.Parse(json.RootElement, current).Newer);
    }
    [Fact]
    public async Task ChecksumAndDownloadAreVerifiedBeforeExtraction()
    {
        using var data = new TestData(); var archive = Path.Combine(data.Paths.Root, "fixture.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("TransMessage.exe").Open()); writer.Write("test executable fixture"); }
        var bytes = await File.ReadAllBytesAsync(archive); var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await ReleaseUpdater.VerifyAsync(archive, hash); await Assert.ThrowsAsync<AppException>(() => ReleaseUpdater.VerifyAsync(archive, new string('0', 64)));
        using var handler = new ScriptedHttp((request, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? new StringContent(hash + "  TransMessage-win-x64.zip\n") : new ByteArrayContent(bytes) }));
        using var http = new HttpClient(handler); var updater = new ReleaseUpdater(http, data.Paths);
        var staged = await updater.DownloadAsync(new("0.2.0", "", Prefix + "TransMessage-win-x64.zip", Prefix + "SHA256SUMS", true)); Assert.True(File.Exists(staged));
    }
    [Fact]
    public async Task InterruptedDownloadDoesNotTouchCurrentExecutable()
    {
        using var data = new TestData(); var current = Path.Combine(data.Paths.Root, "TransMessage.exe"); await File.WriteAllTextAsync(current, "current");
        using var handler = new ScriptedHttp((request, _, _) => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new string('a', 64) + "  TransMessage-win-x64.zip") }) : throw new HttpRequestException("interrupted"));
        using var http = new HttpClient(handler); var updater = new ReleaseUpdater(http, data.Paths);
        await Assert.ThrowsAsync<HttpRequestException>(() => updater.DownloadAsync(new("0.2.0", "", Prefix + "TransMessage-win-x64.zip", Prefix + "SHA256SUMS", true)));
        Assert.Equal("current", await File.ReadAllTextAsync(current)); Assert.Empty(Directory.GetFiles(data.Paths.Root, "*.partial", SearchOption.AllDirectories));
    }
}
