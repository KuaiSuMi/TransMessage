using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TransMessage.Core;
using TransMessage.Core.Channels;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class QqInputTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jA7EAAAAASUVORK5CYII=");

    [Fact]
    public void IncomingQuoteKeepsOnlyNewTextAndImagesAndPreservesReferencePadding()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            id = "new-message",
            author = new { user_openid = "owner" },
            content = " 修复这张图 ",
            timestamp = "2026-10-29T12:34:56+08:00",
            attachments = new[] { new { url = "//images.qq.com/new.png", content_type = "image/png", size = 123 } },
            message_scene = new { ext = new[] { "auth_token=must-not-leak", "msg_idx=this-message", "ref_msg_idx=REFIDX_abc==" } },
            msg_elements = new[] { new { content = "do not execute quoted text", attachments = new[] { new { url = "https://images.qq.com/old.png", content_type = "image/png" } } } }
        });
        var message = QqMessages.Parse("C2C_MESSAGE_CREATE", data)!;
        Assert.Equal("修复这张图", message.Text); Assert.Equal("REFIDX_abc==", message.ReferenceIndex);
        Assert.Equal("https://images.qq.com/new.png", Assert.Single(message.Images).Url);
        Assert.DoesNotContain("must-not-leak", JsonSerializer.Serialize(message));
        Assert.Null(QqMessages.Parse("GROUP_AT_MESSAGE_CREATE", data));
    }

    [Fact]
    public void PureImageMessagesAreAcceptedAndMalformedEventsAreIgnored()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            id = "image",
            author = new { user_openid = "owner" },
            timestamp = DateTimeOffset.UtcNow,
            attachments = new[] { new { url = "https://images.qq.com/image.png", content_type = "image/png" } }
        });
        var message = QqMessages.Parse("C2C_MESSAGE_CREATE", data)!;
        Assert.Equal("", message.Text); Assert.Single(message.Images);
        Assert.Null(QqMessages.Parse("C2C_MESSAGE_CREATE", JsonSerializer.SerializeToElement(1)));
        Assert.Null(QqMessages.Parse("C2C_MESSAGE_CREATE", JsonSerializer.SerializeToElement(new { id = "bad", author = new { user_openid = "owner" }, content = "test", timestamp = "bad" })));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("")]
    public void UnsupportedAttachmentsRejectTheWholeMessageInsteadOfDroppingAttachments(string contentType)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            id = "unsupported",
            author = new { user_openid = "owner" },
            content = "Read this",
            timestamp = DateTimeOffset.UtcNow,
            attachments = new[] { new { url = "https://images.qq.com/file", content_type = contentType } }
        });
        var message = QqMessages.Parse("C2C_MESSAGE_CREATE", data)!;
        Assert.Equal("Read this", message.Text); Assert.NotNull(message.InputError); Assert.Empty(message.Images);
    }

    [Fact]
    public void QuoteWithoutAReferenceIsKeptAsAnErrorAndCannotFallThroughToADraft()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            id = "quote",
            author = new { user_openid = "owner" },
            content = "continue",
            timestamp = DateTimeOffset.UtcNow,
            message_type = 103,
            message_scene = new { ext = new[] { "msg_idx=current", "ref_msg_idx=" } }
        });
        var message = QqMessages.Parse("C2C_MESSAGE_CREATE", data)!;
        Assert.Null(message.ReferenceIndex); Assert.NotNull(message.InputError);
    }

    [Fact]
    public async Task SendsReturnReferenceReceiptsAndIncrementSequencesAcrossRetries()
    {
        var sequences = new List<int>(); var posts = 0; var hosts = new List<string>();
        using var http = new HttpClient(new ScriptedHttp(async (request, _, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal))
                return ScriptedHttp.Json("{\"access_token\":\"test-token\",\"expires_in\":7200}");
            hosts.Add(request.RequestUri.Host);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sequences.Add(body.RootElement.GetProperty("msg_seq").GetInt32());
            posts++;
            if (posts == 1) return new(HttpStatusCode.Unauthorized);
            if (posts == 3) { var rate = new HttpResponseMessage(HttpStatusCode.TooManyRequests); rate.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return rate; }
            return ScriptedHttp.Json("{\"id\":\"sent-id\",\"timestamp\":\"2026-10-29T12:34:56+08:00\",\"ext_info\":{\"ref_idx\":\"REFIDX_sent==\"}}");
        }));
        var qq = new QqClient(http, () => ("test-app", "test-secret")); var target = new QqTarget { Id = "owner" };
        var first = await qq.SendTextAsync(target, "first", CancellationToken.None, "incoming");
        await qq.SendTextAsync(target, "second", CancellationToken.None, "incoming");
        await qq.SendTextAsync(target, "third", CancellationToken.None, "incoming");
        Assert.Equal(new[] { 1, 1, 2, 2, 3 }, sequences); Assert.All(hosts, host => Assert.Equal("api.bot.qq.com", host));
        Assert.Equal("sent-id", first.Id); Assert.Equal("REFIDX_sent==", first.ReferenceIndex);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:34:56+08:00"), first.Timestamp);
    }

    [Fact]
    public async Task OrdinaryNotificationPassesReceiptAndNotificationToRoutingCallback()
    {
        using var data = new TestData(); data.Config.Update(c => c.Qq.Targets.Add(new() { Id = "owner" }));
        using var http = new HttpClient(new ScriptedHttp((request, _, _) => Task.FromResult(ScriptedHttp.Json(
            request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal)
                ? "{\"access_token\":\"token\",\"expires_in\":7200}" : "{\"id\":\"sent\",\"ext_info\":{\"ref_idx\":\"reference\"}}"))));
        var notification = new Notification { Id = "notice", Message = "done" }; var called = false;
        var channel = new QqOfficialChannel(data.Config, new(http, () => ("app", "secret")), (target, notice, receipt) =>
        {
            called = true; Assert.Equal("owner", target.Id); Assert.Same(notification, notice); Assert.Equal("reference", receipt.ReferenceIndex); return Task.CompletedTask;
        });
        await channel.SendAsync(notification, CancellationToken.None); Assert.True(called);
    }

    [Fact]
    public void ReferenceStorePersistsEncryptedOwnerScopedMappingsAndPreservesCorruptFile()
    {
        using var data = new TestData(); var store = new QqReferenceStore(data.Paths);
        store.Save("owner", "REFIDX_reference==", CodexTests.ThreadId, "AB12CD34");
        var reopened = new QqReferenceStore(data.Paths);
        Assert.Equal(new QqReference(CodexTests.ThreadId, "AB12CD34"), reopened.Find("owner", "REFIDX_reference=="));
        Assert.Null(reopened.Find("stranger", "REFIDX_reference=="));
        var file = Path.Combine(data.Paths.Root, "qq-references.dpapi");
        Assert.DoesNotContain("REFIDX_reference", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        File.WriteAllText(file, "broken");
        Assert.Throws<CryptographicException>(() => store.Save("owner", "new", CodexTests.ThreadId));
        Assert.Equal("broken", File.ReadAllText(file));
    }

    [Fact]
    public void ReferencesExpireAfter30DaysAndStayBounded()
    {
        using var data = new TestData();
        var entries = Enumerable.Range(0, 2005).Select(i => new
        {
            Owner = "owner",
            Index = "index-" + i,
            ThreadId = CodexTests.ThreadId,
            QuestionCode = (string?)null,
            Time = i == 2004 ? DateTimeOffset.UtcNow.AddDays(-31) : DateTimeOffset.UtcNow.AddMinutes(-2005 + i)
        });
        var file = Path.Combine(data.Paths.Root, "qq-references.dpapi");
        File.WriteAllBytes(file, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(entries), null, DataProtectionScope.CurrentUser));
        var store = new QqReferenceStore(data.Paths); Assert.Null(store.Find("owner", "index-2004"));
        store.Save("owner", "new", CodexTests.ThreadId);
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try { using var json = JsonDocument.Parse(plain); Assert.Equal(2000, json.RootElement.GetArrayLength()); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Assert.NotNull(store.Find("owner", "new"));
    }

    [Fact]
    public async Task ImagesAreCachedByActualFormatAndOldCacheIsPrunedWithoutDeletingOtherFiles()
    {
        using var data = new TestData(); var directory = Path.Combine(data.Paths.Root, "qq-images"); Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(expired, Png); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-31));
        var unrelated = Path.Combine(directory, "user-file.png"); File.WriteAllBytes(unrelated, Png); File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-31));
        using var http = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) })));
        using (var cache = new QqImageCache(data.Paths, http))
        {
            var path = Assert.Single(await cache.DownloadAsync([new("https://images.qq.com/name.jpg?token=private", "image/jpeg")]));
            Assert.Equal(".png", Path.GetExtension(path)); Assert.Equal(Png, await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain("private", path); Assert.False(File.Exists(expired)); Assert.True(File.Exists(unrelated));
        }
        using var response = await http.GetAsync("https://images.qq.com/alive"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://example.com/image.png")]
    [InlineData("https://127.0.0.1/image.png")]
    [InlineData("https://10.0.0.1/image.png")]
    [InlineData("https://172.16.1.1/image.png")]
    [InlineData("https://192.168.1.1/image.png")]
    [InlineData("https://169.254.169.254/image.png")]
    [InlineData("https://[::1]/image.png")]
    [InlineData("https://[::ffff:127.0.0.1]/image.png")]
    [InlineData("https://example.com:444/image.png")]
    [InlineData("https://user:pass@example.com/image.png")]
    public async Task PrivateOrUnsafeImageAddressesAreRejectedBeforeHttp(string url)
    {
        using var data = new TestData(); var calls = 0;
        using var http = new HttpClient(new ScriptedHttp((_, _, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        using var cache = new QqImageCache(data.Paths, http);
        var error = await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync([new(url, "image/png")]));
        Assert.Equal("QQ_IMAGE_URL", error.Code); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InvalidOrRedirectedImageRollsBackAllFilesInTheBatch()
    {
        using var data = new TestData();
        using var http = new HttpClient(new ScriptedHttp((_, call, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(call == 1 ? Png : Encoding.UTF8.GetBytes("<html>this is not an image despite its declared MIME type</html>")) })));
        using var cache = new QqImageCache(data.Paths, http);
        var error = await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync([new("https://images.qq.com/1.png", "image/png"), new("https://images.qq.com/2.png", "image/png")]));
        Assert.Equal("QQ_IMAGE_FORMAT", error.Code); Assert.Empty(Directory.GetFiles(Path.Combine(data.Paths.Root, "qq-images")));
        using var redirectHttp = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://127.0.0.1/secret") } })));
        using var redirectCache = new QqImageCache(data.Paths, redirectHttp);
        Assert.Equal("QQ_IMAGE_URL", (await Assert.ThrowsAsync<AppException>(() => redirectCache.DownloadAsync([new("https://images.qq.com/redirect", "image/png")]))).Code);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("gif")]
    public async Task WindowsDecoderValidatesSupportedImagesWithoutChangingTheirBytes(string format)
    {
        var bytes = EncodeImage(format);
        using var data = new TestData();
        using var http = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        using var cache = new QqImageCache(data.Paths, http);
        var path = Assert.Single(await cache.DownloadAsync([new("https://images.qq.com/image", "image/unknown")]));
        Assert.Equal("." + format, Path.GetExtension(path)); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("png-header")]
    [InlineData("jpeg-header")]
    [InlineData("gif-header")]
    [InlineData("png-missing-pixels")]
    [InlineData("unsupported-bmp")]
    public async Task TruncatedHeadersMissingPixelsAndUnsupportedFormatsAreRejected(string kind)
    {
        var bytes = kind switch
        {
            "png-header" => Png[..16],
            "jpeg-header" => new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 16, 74, 70, 73, 70 },
            "gif-header" => "GIF89a\u0001\u0000\u0001\u0000"u8.ToArray(),
            "png-missing-pixels" => Png[..33].Concat(Png[^12..]).ToArray(),
            _ => EncodeImage("bmp")
        };
        using var data = new TestData();
        using var http = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        using var cache = new QqImageCache(data.Paths, http);
        Assert.Equal("QQ_IMAGE_FORMAT", (await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync([new("https://images.qq.com/image", "image/png")]))).Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Paths.Root, "qq-images")));
    }

    [Fact]
    public async Task ImageCountsDeclaredSizesAndStreamedSizesAreBounded()
    {
        using var data = new TestData(); var calls = 0;
        using var http = new HttpClient(new ScriptedHttp((_, _, _) =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnboundedImageStream()) });
        }));
        using var cache = new QqImageCache(data.Paths, http);
        var image = new QqImageAttachment("https://images.qq.com/1.png", "image/png");
        Assert.Equal("QQ_IMAGE_COUNT", (await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync(Enumerable.Repeat(image, QqImageCache.MaximumImageCount + 1).ToArray()))).Code);
        Assert.Equal("QQ_IMAGE_SIZE", (await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync([image with { Size = QqImageCache.MaximumImageBytes + 1L }]))).Code);
        Assert.Equal(0, calls);
        Assert.Equal("QQ_IMAGE_SIZE", (await Assert.ThrowsAsync<AppException>(() => cache.DownloadAsync([image]))).Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Paths.Root, "qq-images")));
    }

    [Fact]
    public async Task CancellationRemovesPartialDownloads()
    {
        using var data = new TestData(); using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnboundedImageStream(stop)) })));
        using var cache = new QqImageCache(data.Paths, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.DownloadAsync([new("https://images.qq.com/1.png", "image/png")], stop.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Paths.Root, "qq-images")));
    }

    private static byte[] EncodeImage(string format)
    {
        BitmapEncoder encoder = format switch { "png" => new PngBitmapEncoder(), "jpg" => new JpegBitmapEncoder(), "gif" => new GifBitmapEncoder(), _ => new BmpBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class UnboundedImageStream(CancellationTokenSource? stop = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); buffer.Span.Clear(); stop?.Cancel(); return ValueTask.FromResult(buffer.Length);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
