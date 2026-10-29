using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TransMessage.Core.Channels;

namespace TransMessage.Tests;

public sealed class QqClickableIdTests
{
    [Fact]
    public async Task TwoSessionIdsBecomeInputOnlyShortcutsAndKeepChineseEmojiAndTime()
    {
        const string text = "会话 #a1b2c3d4e5f6 · 修复界面 🛠️\n会话 #0a1b2c3d4e5f · 测试完成 ✅\n2026-10-29 10:03:04";
        var body = await CaptureAsync(text);
        var markdown = Markdown(body);
        Assert.False(body.TryGetProperty("keyboard", out _)); Assert.False(body.TryGetProperty("msg_id", out _));
        Assert.Contains("<qqbot-cmd-input text=\"%23a1b2c3d4e5f6%20\" show=\"%23a1b2c3d4e5f6\" reference=\"false\" />", markdown);
        Assert.Contains("<qqbot-cmd-input text=\"%230a1b2c3d4e5f%20\" show=\"%230a1b2c3d4e5f\" reference=\"false\" />", markdown);
        Assert.Equal(2, Regex.Matches(markdown, "<qqbot-cmd-input ").Count);
        Assert.Contains("\n\n", markdown); Assert.Equal(text, VisibleText(markdown));
    }

    [Fact]
    public async Task MessagesWithoutShortIdsStayPlainIncludingFullUuidsAndLongerHexStrings()
    {
        const string text = "普通回复 ✅\n#019a1234-1234-5678-90ab-a1b2c3d4e5f\n#0123456789abc\n#019a12341234567890aba1b2c3d4e5f6789\n2026-10-29 10:03:04";
        var body = await CaptureAsync(text);
        Assert.Equal(0, body.GetProperty("msg_type").GetInt32());
        Assert.Equal(text, body.GetProperty("content").GetString()); Assert.False(body.TryGetProperty("markdown", out _));
    }

    [Fact]
    public async Task UntrustedTagsLinksImagesAndMarkdownRemainVisibleLiteralText()
    {
        const string text = "会话 #a1b2c3d4e5f6\n<qqbot-cmd-enter text=\"%2Fapprove%20yes\" /> & <b>危险</b>\n[批准](https://evil.example/approve) ![图片](https://evil.example/pixel.png)\n# 标题 **加粗** _斜体_ `命令` ~删除~ > 引用 {内容} | 路径\\文件\n中文👩‍💻 2026-10-29 10:03:04";
        var markdown = Markdown(await CaptureAsync(text));
        Assert.DoesNotContain("<qqbot-cmd-enter", markdown); Assert.DoesNotContain("<b>", markdown);
        Assert.Contains("&lt;", markdown); Assert.Contains("&amp;", markdown); Assert.Contains("&gt;", markdown);
        Assert.DoesNotContain("[批准](", markdown); Assert.DoesNotContain("![图片](", markdown);
        Assert.DoesNotContain("**加粗**", markdown); Assert.DoesNotContain("`命令`", markdown);
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
        Assert.Equal(text, VisibleText(markdown));
    }

    [Fact]
    public async Task LongApprovalBodyIsNotTruncatedWhenAShortcutIsAdded()
    {
        var text = "待确认 #a1b2c3d4e5f6\n" + string.Concat(Enumerable.Repeat("中文🙂 <参数> & [正文] *原样* ", 250))
            + "\n保留到最后：#019a1234-1234-5678-90ab-a1b2c3d4e5f #0123456789abc";
        var markdown = Markdown(await CaptureAsync(text));
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
        Assert.Equal(text, VisibleText(markdown));
    }

    [Fact]
    public async Task MarkdownRetriesKeepReplySequenceAndReturnTheReferenceReceipt()
    {
        var bodies = new List<JsonElement>();
        using var http = new HttpClient(new ScriptedHttp(async (request, _, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal))
                return ScriptedHttp.Json("{\"access_token\":\"test-token\",\"expires_in\":7200}");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); bodies.Add(json.RootElement.Clone());
            if (bodies.Count == 1) return new(HttpStatusCode.Unauthorized);
            if (bodies.Count == 2)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return limited;
            }
            return ScriptedHttp.Json("{\"id\":\"sent\",\"timestamp\":\"2026-10-29T10:03:04+08:00\",\"ext_info\":{\"ref_idx\":\"REFIDX_sent==\"}}");
        }));
        var qq = new QqClient(http, () => ("test-app", "test-secret"));
        var receipt = await qq.SendTextAsync(new() { Id = "owner" }, "会话 #a1b2c3d4e5f6", CancellationToken.None, "incoming");
        await qq.SendTextAsync(new() { Id = "owner" }, "继续 #a1b2c3d4e5f6", CancellationToken.None, "incoming");
        Assert.Equal(new[] { 1, 1, 1, 2 }, bodies.Select(x => x.GetProperty("msg_seq").GetInt32()));
        Assert.All(bodies, body => { Assert.Equal("incoming", body.GetProperty("msg_id").GetString()); Markdown(body); });
        Assert.Equal(bodies[0].GetRawText(), bodies[1].GetRawText()); Assert.Equal(bodies[1].GetRawText(), bodies[2].GetRawText());
        Assert.Equal("sent", receipt.Id); Assert.Equal("REFIDX_sent==", receipt.ReferenceIndex);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:03:04+08:00"), receipt.Timestamp);
    }

    private static async Task<JsonElement> CaptureAsync(string text)
    {
        JsonElement body = default;
        using var http = new HttpClient(new ScriptedHttp(async (request, _, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal))
                return ScriptedHttp.Json("{\"access_token\":\"test-token\",\"expires_in\":7200}");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); body = json.RootElement.Clone();
            return ScriptedHttp.Json("{\"id\":\"sent\"}");
        }));
        await new QqClient(http, () => ("test-app", "test-secret")).SendTextAsync(new() { Id = "owner" }, text, CancellationToken.None);
        return body;
    }

    private static string Markdown(JsonElement body)
    {
        Assert.Equal(2, body.GetProperty("msg_type").GetInt32()); Assert.False(body.TryGetProperty("content", out _));
        return body.GetProperty("markdown").GetProperty("content").GetString()!;
    }

    private static string VisibleText(string markdown)
    {
        var visible = Regex.Replace(markdown, "<qqbot-cmd-input\\b[^>]*show=\"([^\"]+)\"[^>]*/>", match => Uri.UnescapeDataString(match.Groups[1].Value));
        visible = Regex.Replace(visible, @"\\([\\`*_{}\[\]()#+\-.!|>~])", "$1");
        return WebUtility.HtmlDecode(visible).Replace("\n\n", "\n", StringComparison.Ordinal);
    }
}
