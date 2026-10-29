using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Channels;

namespace TransMessage.Tests;

public sealed class QqFileTests
{
    [Fact]
    public async Task SmallFilesUseBase64ThenAFileCardWithSharedSequenceAndStableRetries()
    {
        using var data = new TestData(); var bytes = Encoding.UTF8.GetBytes("实际报告\n中文与 emoji 📄\n");
        var path = Path.Combine(data.Paths.Root, "报告📄.md"); await File.WriteAllBytesAsync(path, bytes);
        var uploads = new List<string>(); var messages = new List<JsonElement>(); var filePosts = 0;
        using var http = ApiHttp((request, body, ct) =>
        {
            Assert.Equal("api.bot.qq.com", request.RequestUri!.Host);
            Assert.Equal("QQBot", request.Headers.Authorization!.Scheme);
            if (request.RequestUri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                uploads.Add(body.GetRawText()); Assert.Equal(4, body.GetProperty("file_type").GetInt32());
                Assert.Equal("报告📄.md", body.GetProperty("file_name").GetString()); Assert.False(body.GetProperty("srv_send_msg").GetBoolean());
                Assert.False(body.TryGetProperty("url", out _)); Assert.False(body.TryGetProperty("msg_seq", out _));
                Assert.Equal(bytes, Convert.FromBase64String(body.GetProperty("file_data").GetString()!));
                Assert.Throws<IOException>(() => { using var blockedWriter = File.OpenWrite(path); });
                return Task.FromResult(uploads.Count == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : ScriptedHttp.Json("{\"file_info\":\"FILE_OPAQUE\",\"ttl\":0}"));
            }
            messages.Add(body.Clone());
            if (body.GetProperty("msg_type").GetInt32() == 7)
            {
                Assert.Throws<IOException>(() => { using var blockedWriter = File.OpenWrite(path); });
                Assert.Equal("FILE_OPAQUE", body.GetProperty("media").GetProperty("file_info").GetString());
                Assert.False(body.TryGetProperty("content", out _)); Assert.False(body.TryGetProperty("file_data", out _));
                if (++filePosts == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1));
                    return Task.FromResult(limited);
                }
            }
            return Task.FromResult(Receipt());
        });
        var qq = new QqClient(http, () => ("test-app", "test-secret"));
        await qq.SendTextAsync(new() { Id = "owner" }, "开始", CancellationToken.None, "command");
        var receipt = await qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command");
        await qq.SendTextAsync(new() { Id = "owner" }, "完成", CancellationToken.None, "command");
        Assert.Equal(uploads[0], uploads[1]); Assert.Equal(new[] { 1, 2, 2, 3 }, messages.Select(x => x.GetProperty("msg_seq").GetInt32()));
        Assert.All(messages, x => Assert.Equal("command", x.GetProperty("msg_id").GetString()));
        Assert.Equal(messages[1].GetRawText(), messages[2].GetRawText());
        Assert.Equal(new QqSendReceipt("file-message", DateTimeOffset.Parse("2026-10-29T12:34:56+08:00"), "REFIDX_file=="), receipt);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExactlyFiveMiBUsesDirectUploadWithoutConsumingAProactiveReplySequence()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "boundary.bin"); await File.WriteAllBytesAsync(path, new byte[QqClient.DirectFileBytes]);
        var steps = new List<string>();
        using var http = ApiHttp((request, body, ct) =>
        {
            steps.Add(request.RequestUri!.AbsolutePath);
            if (steps.Count == 1)
            {
                Assert.Equal(QqClient.DirectFileBytes, Convert.FromBase64String(body.GetProperty("file_data").GetString()!).Length);
                return Task.FromResult(ScriptedHttp.Json("{\"file_info\":\"direct\"}"));
            }
            Assert.False(body.TryGetProperty("msg_id", out _)); Assert.False(body.TryGetProperty("msg_seq", out _));
            return Task.FromResult(Receipt());
        });
        await new QqClient(http, () => ("app", "secret")).SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None);
        Assert.Equal(new[] { "/v2/users/owner/files", "/v2/users/owner/messages" }, steps);
    }

    [Fact]
    public async Task LargeFilesUseTheOfficialHashPartitionFinishAndMergeProtocol()
    {
        using var data = new TestData(); var bytes = new byte[10_002_432 + 37]; Random.Shared.NextBytes(bytes);
        var path = Path.Combine(data.Paths.Root, "large.zip"); await File.WriteAllBytesAsync(path, bytes);
        var finished = new List<int>(); var uploaded = new List<int>(); var merged = false; var sent = false;
        var putAttempt = 0; const int blockSize = QqClient.DirectFileBytes;
        using var parts = new HttpClient(new ScriptedHttp(async (request, _, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method); Assert.Null(request.Headers.Authorization);
            var index = int.Parse(request.RequestUri!.AbsolutePath.Trim('/'));
            var expected = bytes.AsSpan(index * blockSize, Math.Min(blockSize, bytes.Length - index * blockSize)).ToArray();
            Assert.Equal(expected, await request.Content!.ReadAsByteArrayAsync(ct)); Assert.Equal(expected.Length, request.Content.Headers.ContentLength);
            uploaded.Add(index);
            return new HttpResponseMessage(++putAttempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));
        using var http = ApiHttp((request, body, ct) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/upload_prepare", StringComparison.Ordinal))
            {
                Assert.Equal(4, body.GetProperty("file_type").GetInt32()); Assert.Equal(bytes.Length.ToString(), body.GetProperty("file_size").GetString());
                Assert.Equal("large.zip", body.GetProperty("file_name").GetString());
                Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), body.GetProperty("md5").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(bytes)), body.GetProperty("sha1").GetString());
                Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes.AsSpan(0, 10_002_432))), body.GetProperty("md5_10m").GetString());
                return Task.FromResult(Prepared(bytes.Length, blockSize, reverse: true));
            }
            if (route.EndsWith("/upload_part_finish", StringComparison.Ordinal))
            {
                var index = body.GetProperty("part_index").GetInt32(); var size = Math.Min(blockSize, bytes.Length - index * blockSize);
                Assert.Equal("upload-id", body.GetProperty("upload_id").GetString()); Assert.Equal(size.ToString(), body.GetProperty("block_size").GetString());
                Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes.AsSpan(index * blockSize, size))), body.GetProperty("md5").GetString());
                finished.Add(index); return Task.FromResult(ScriptedHttp.Json("{}"));
            }
            if (route.EndsWith("/files", StringComparison.Ordinal))
            {
                Assert.Equal(new[] { 0, 1 }, finished); Assert.False(body.GetProperty("srv_send_msg").GetBoolean());
                Assert.Equal("upload-id", body.GetProperty("upload_id").GetString()); Assert.False(body.TryGetProperty("file_data", out _));
                merged = true; return Task.FromResult(ScriptedHttp.Json("{\"file_info\":\"merged\"}"));
            }
            Assert.True(merged); Assert.Equal("merged", body.GetProperty("media").GetProperty("file_info").GetString());
            Assert.Equal(1, body.GetProperty("msg_seq").GetInt32()); sent = true; return Task.FromResult(Receipt());
        });
        var receipt = await new QqClient(http, () => ("app", "secret"), uploadHttp: parts).SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command");
        Assert.True(sent); Assert.Equal(new[] { 0, 0, 1 }, uploaded); Assert.Equal("REFIDX_file==", receipt.ReferenceIndex);
        Assert.Equal(bytes.Length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("http://uploads.qq.com/0", "QQ_FILE_URL")]
    [InlineData("https://127.0.0.1/0", "QQ_FILE_URL")]
    [InlineData("https://192.168.1.1/0", "QQ_FILE_URL")]
    [InlineData("https://[::ffff:127.0.0.1]/0", "QQ_FILE_URL")]
    [InlineData("https://user:password@uploads.qq.com/0", "QQ_FILE_URL")]
    [InlineData("bad-layout", "QQ_FILE_SCHEMA")]
    public async Task UnsafeAddressesAndInvalidPartsStopBeforeAnyUpload(string address, string code)
    {
        using var data = new TestData(); var path = LargeFile(data); var putCount = 0; var calls = 0;
        using var parts = new HttpClient(new ScriptedHttp((_, _, _) => { putCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        using var http = ApiHttp((_, _, _) =>
        {
            calls++;
            return Task.FromResult(address == "bad-layout" ? ScriptedHttp.Json("{\"upload_id\":\"id\",\"block_size\":\"5242880\",\"parts\":[]}")
                : Prepared(QqClient.DirectFileBytes + 1, QqClient.DirectFileBytes, address: address));
        });
        var error = await Assert.ThrowsAsync<AppException>(() => new QqClient(http, () => ("app", "secret"), uploadHttp: parts)
            .SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None));
        Assert.Equal(code, error.Code); Assert.Equal(0, putCount); Assert.Equal(1, calls); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RedirectedAndCancelledPartsNeverMergeOrSendAndLeaveTheSourceFile()
    {
        using var data = new TestData(); var path = LargeFile(data); var calls = 0;
        using var http = ApiHttp((_, _, _) => { calls++; return Task.FromResult(Prepared(QqClient.DirectFileBytes + 1, QqClient.DirectFileBytes)); });
        using var redirect = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new("https://127.0.0.1/private") } })));
        Assert.Equal("QQ_FILE_URL", (await Assert.ThrowsAsync<AppException>(() => new QqClient(http, () => ("app", "secret"), uploadHttp: redirect)
            .SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None))).Code);
        using var stop = new CancellationTokenSource();
        using var cancelled = new HttpClient(new ScriptedHttp((_, _, ct) => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QqClient(http, () => ("app", "secret"), uploadHttp: cancelled)
            .SendFileAsync(new() { Id = "owner" }, path, stop.Token));
        Assert.Equal(2, calls); Assert.Equal(QqClient.DirectFileBytes + 1, new FileInfo(path).Length);
    }

    [Fact]
    public async Task EmptyOversizedAndNonPrivateFilesAreRejectedBeforeNetwork()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "size.bin"); using (File.Create(path)) { }
        var calls = 0; using var http = ApiHttp((_, _, _) => { calls++; return Task.FromResult(Receipt()); });
        var qq = new QqClient(http, () => ("app", "secret"));
        Assert.Equal("QQ_FILE_SIZE", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None))).Code);
        using (var file = File.OpenWrite(path)) file.SetLength(QqClient.MaximumFileBytes + 1);
        Assert.Equal("QQ_FILE_SIZE", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None))).Code);
        Assert.Equal("QQ_FILE_TARGET", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "group", Kind = "group" }, path, CancellationToken.None))).Code);
        Assert.Equal(0, calls); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MissingUploadReceiptDoesNotSendOrConsumeAReplySlotAndFifthReplyIsBlocked()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "report.txt"); await File.WriteAllTextAsync(path, "report");
        var calls = 0; var sequences = new List<int>();
        using var http = ApiHttp((request, body, ct) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)) return Task.FromResult(ScriptedHttp.Json("{}"));
            sequences.Add(body.GetProperty("msg_seq").GetInt32()); return Task.FromResult(Receipt());
        });
        var qq = new QqClient(http, () => ("app", "secret"));
        Assert.Equal("QQ_FILE_SCHEMA", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command"))).Code);
        for (var i = 0; i < 4; i++) await qq.SendTextAsync(new() { Id = "owner" }, "text", CancellationToken.None, "command");
        Assert.Equal("QQ_REPLY_LIMIT", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command"))).Code);
        Assert.Equal("QQ_REPLY_LIMIT", (await Assert.ThrowsAsync<AppException>(() => qq.SendTextAsync(new() { Id = "owner" }, "fifth", CancellationToken.None, "command"))).Code);
        Assert.Equal(new[] { 1, 2, 3, 4 }, sequences); Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData(false, "QQ_500_0")]
    [InlineData(true, "QQ_DELIVERY_UNCERTAIN")]
    public async Task AmbiguousFileMessageFailuresAreNotRetriedOrReportedAsUploadFailures(bool interrupted, string code)
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "report.txt"); await File.WriteAllTextAsync(path, "report");
        var messagePosts = 0;
        using var http = ApiHttp((request, body, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)) return Task.FromResult(ScriptedHttp.Json("{\"file_info\":\"ready\"}"));
            messagePosts++;
            if (interrupted) throw new TaskCanceledException("Simulated response timeout");
            return Task.FromResult(ScriptedHttp.Json("{}", HttpStatusCode.InternalServerError));
        });
        var error = await Assert.ThrowsAsync<AppException>(() => new QqClient(http, () => ("app", "secret"))
            .SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command"));
        Assert.Equal(code, error.Code); Assert.Equal(1, messagePosts); Assert.Equal("report", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OneHeaderAndThreeFilesUseExactlyFourRepliesAndRejectAnotherBeforeUploading()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "report.txt"); await File.WriteAllTextAsync(path, "report");
        var uploads = 0; var sequences = new List<int>();
        using var http = ApiHttp((request, body, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            { uploads++; return Task.FromResult(ScriptedHttp.Json("{\"file_info\":\"ready\"}")); }
            sequences.Add(body.GetProperty("msg_seq").GetInt32()); return Task.FromResult(Receipt());
        });
        var qq = new QqClient(http, () => ("app", "secret"));
        await qq.SendTextAsync(new() { Id = "owner" }, "附件 1-3", CancellationToken.None, "command");
        for (var i = 0; i < 3; i++) await qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command");
        Assert.Equal("QQ_REPLY_LIMIT", (await Assert.ThrowsAsync<AppException>(() => qq.SendFileAsync(new() { Id = "owner" }, path, CancellationToken.None, "command"))).Code);
        Assert.Equal(new[] { 1, 2, 3, 4 }, sequences); Assert.Equal(3, uploads);
    }

    private static HttpClient ApiHttp(Func<HttpRequestMessage, JsonElement, CancellationToken, Task<HttpResponseMessage>> respond) => new(new ScriptedHttp(async (request, _, ct) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal))
            return ScriptedHttp.Json("{\"access_token\":\"test-token\",\"expires_in\":7200}");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); return await respond(request, json.RootElement, ct);
    }));
    private static HttpResponseMessage Receipt() => ScriptedHttp.Json("{\"id\":\"file-message\",\"timestamp\":\"2026-10-29T12:34:56+08:00\",\"ext_info\":{\"ref_idx\":\"REFIDX_file==\"}}");
    private static HttpResponseMessage Prepared(int size, int blockSize, bool reverse = false, string? address = null)
    {
        var parts = Enumerable.Range(0, (size + blockSize - 1) / blockSize).Select(index => new
        {
            index,
            block_size = Math.Min(blockSize, size - index * blockSize).ToString(),
            presigned_url = address ?? "https://uploads.qq.com/" + index
        });
        return ScriptedHttp.Json(JsonSerializer.Serialize(new { upload_id = "upload-id", block_size = blockSize.ToString(), parts = reverse ? parts.Reverse() : parts, upload_config = new { retry_timeout = 10, retry_delay = 0 } }));
    }
    private static string LargeFile(TestData data)
    {
        var path = Path.Combine(data.Paths.Root, "large.bin"); using var file = File.Create(path); file.SetLength(QqClient.DirectFileBytes + 1); return path;
    }
}
