using System.Text;
using TransMessage.Core;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Tests;

public sealed class NotificationTests
{
    [Theory]
    [InlineData("sk-proj-abcdefghijk_987654321")]
    [InlineData("ghp_abcdefghijk123456789")]
    [InlineData("github_pat_123456789abcdefghijk")]
    [InlineData("AKIA1234567890ABCDEF")]
    [InlineData("eyJabc123.abcdefghijk.abcdefghijk")]
    [InlineData("Authorization: Bearer 123456789-secret")]
    [InlineData("Cookie: session=123456789")]
    [InlineData("Password=badpassword")]
    [InlineData("OPENAI_API_KEY='secret value'")]
    [InlineData("{\"AppSecret\":\"my secret\"}")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nprivate-key-material\n-----END RSA PRIVATE KEY-----")]
    public void SecretsAreRedacted(string secret) => Assert.Contains("[REDACTED]", new Sanitizer().Redact(secret));
    [Fact]
    public void ExactSecretsAndNullsAreRedacted()
    {
        var sanitizer = new Sanitizer(() => ["a-specific-qq-secret"]);
        Assert.Equal("[REDACTED]", sanitizer.Redact("a-specific-qq-secret")); Assert.Equal("", sanitizer.Redact(null));
    }
    [Fact]
    public void UnicodeTruncationStaysValid()
    {
        var value = string.Concat(Enumerable.Repeat("好😀", 2000)); var truncated = Sanitizer.Truncate(value, 100);
        Assert.True(truncated.Length <= 100); Assert.Contains("已截断", truncated);
        var text = Sanitizer.QqText(new() { Message = value }); Assert.True(Encoding.UTF8.GetByteCount(text) <= 4000);
        Assert.DoesNotContain("�", text);
    }
    [Fact]
    public void QqRepliesIncludeTimestampAndTimeZone()
    {
        var notification = new Notification { Timestamp = DateTimeOffset.Parse("2026-10-15T12:34:56.1234567+05:30", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.StartsWith("原始时间：2026-10-15T12:34:56.1234567+05:30\n", Sanitizer.QqText(notification));
        var received = Sanitizer.QqText(notification with { Metadata = new() { ["timestamp_source"] = "received" } });
        Assert.StartsWith("接收时间（来源未提供原始时间）：", received);
    }
    [Fact]
    public void NormalizeAndValidate()
    {
        var clean = new Sanitizer().Clean(new() { Title = " hi\0 ", Message = " password=secret ", Tags = ["Bearer abcd"], Metadata = new() { ["field"] = "secret=asdf" } });
        Assert.Equal("hi", clean.Title); Assert.Equal("[REDACTED]", clean.Message); Assert.Equal("[REDACTED]", clean.Tags[0]);
        Assert.Throws<AppException>(() => new Notification { Level = "other" }.Validate());
        Assert.Throws<AppException>(() => new Notification { Title = null! }.Validate());
    }
    [Fact]
    public async Task ConcurrentAndRestartDuplicatesSendOnce()
    {
        using var data = new TestData(); var channel = new RecordingChannel();
        var first = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        var second = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        var n = new Notification { Id = "same-turn", Message = "hello" };
        var results = await Task.WhenAll(first.SendAsync(n), second.SendAsync(n), first.SendAsync(n));
        Assert.Single(channel.Sent); Assert.Equal(2, results.Count(r => r.Status == "duplicate"));
    }
    [Fact]
    public void DpapiNeverWritesClearSecret()
    {
        using var data = new TestData(); var store = new SecretStore(data.Paths); store.Set("qq", "never-in-a-file");
        Assert.Equal("never-in-a-file", store.Get("qq"));
        Assert.DoesNotContain("never-in-a-file", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(data.Paths.Root, "qq.dpapi"))));
        Assert.Equal(64, store.ApiToken().Length); Assert.NotEqual(store.ApiToken(), store.RegenerateApiToken());
    }
    [Fact]
    public void ConfigRoundTripAndInvalidConfigIsNotReplaced()
    {
        using var data = new TestData(); data.Config.Update(c => { c.Qq.AppId = "123"; c.Qq.Targets.Add(new() { Id = "abc", Name = "我的手机" }); });
        Assert.Equal("我的手机", data.Config.Load().Qq.Targets.Single().Name);
        Assert.Contains("app_id", File.ReadAllText(data.Paths.Config));
        File.WriteAllText(data.Paths.Config, "[invalid"); Assert.Throws<AppException>(() => data.Config.Load()); Assert.Equal("[invalid", File.ReadAllText(data.Paths.Config));
    }
}
