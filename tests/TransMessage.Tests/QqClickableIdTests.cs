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
        Assert.Contains("\n", markdown); Assert.Contains("修复界面 🛠️", markdown); Assert.Contains("测试完成 ✅", markdown);
        Assert.Contains("2026-10-29 10:03:04", markdown); Assert.DoesNotContain("&", markdown);
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
    public async Task WebLinksKeepTheirSyntaxAndSourceCommandTagsStayInactive()
    {
        const string text = "会话 #a1b2c3d4e5f6\n<qqbot-cmd-enter text=\"%2Fapprove%20yes\" />\n[发布](https://example.com/a-b?q=1&n=2) ![图片](https://example.com/pixel.png)\n**加粗** `命令`\n中文👩‍💻 2026-10-29 10:03:04";
        var markdown = Markdown(await CaptureAsync(text));
        Assert.DoesNotContain("<qqbot-cmd-enter", markdown);
        Assert.DoesNotContain("&lt;", markdown); Assert.DoesNotContain("&amp;", markdown); Assert.DoesNotContain("\\[", markdown);
        Assert.Contains("[发布](https://example.com/a-b?q=1&n=2)", markdown);
        Assert.DoesNotContain("![图片](", markdown); Assert.Contains("[图片](https://example.com/pixel.png)", markdown);
        Assert.Contains("**加粗**", markdown); Assert.Contains("`命令`", markdown); Assert.Contains("中文👩‍💻", markdown);
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
    }

    [Fact]
    public async Task LongApprovalBodyIsNotTruncatedWhenAShortcutIsAdded()
    {
        var text = "待确认 #a1b2c3d4e5f6\n" + string.Concat(Enumerable.Repeat("中文🙂 <参数> & [正文] *原样* ", 250))
            + "\n保留到最后：#019a1234-1234-5678-90ab-a1b2c3d4e5f #0123456789abc";
        var markdown = Markdown(await CaptureAsync(text));
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
        Assert.Contains("保留到最后：#019a1234-1234-5678-90ab-a1b2c3d4e5f #0123456789abc", markdown);
        Assert.Equal(250, Regex.Matches(markdown, "中文🙂").Count);
        Assert.DoesNotContain("已截断", markdown);
    }

    [Fact]
    public async Task UrlFragmentsAndLinkLabelsNeverBecomeSessionActions()
    {
        const string url = "https://example.com/page#a1b2c3d4e5f6";
        var markdown = Markdown(await CaptureAsync($"会话 #0a1b2c3d4e5f\n[网页 #a1b2c3d4e5f6]({url})\n{url}"));
        Assert.Contains($"[网页 #a1b2c3d4e5f6]({url})", markdown);
        Assert.Contains($"<{url}>", markdown);
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
    }

    [Fact]
    public async Task BareUrlsWithoutSessionNumbersAreClickableMarkdown()
    {
        const string url = "https://example.com/a_b?q=1&n=2#abcdef123456";
        var markdown = Markdown(await CaptureAsync("下载 " + url));
        Assert.Contains("<" + url + ">", markdown); Assert.DoesNotContain("qqbot-cmd-input", markdown);
    }

    [Fact]
    public async Task StandaloneInlineSessionCodeIsClickableButOrdinaryCodeRemainsCode()
    {
        var markdown = Markdown(await CaptureAsync("会话 `#a1b2c3d4e5f6`\n`echo #0a1b2c3d4e5f`\n```text\n#abcdef123456\n```"));
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
        Assert.DoesNotContain("`<qqbot-cmd-input", markdown);
        Assert.Contains("`echo #0a1b2c3d4e5f`", markdown); Assert.Contains("#abcdef123456", markdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFilesUseReadableLabelsAndOnlyLinkedTurnsOfferQuotedFileCommand(bool linkFiles)
    {
        var markdown = Markdown(await CaptureAsync("会话 #abcdef123456\n[测试附件](<D:/文档/我的 项目/文件(1).txt>)", linkFiles));
        Assert.DoesNotContain("D:/", markdown); Assert.DoesNotContain("\\[", markdown);
        if (linkFiles)
        {
            var tag = Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input[^>]+reference=\"true\"[^>]*/>"));
            Assert.Contains("text=\"%2Ffile\"", tag.Value);
            Assert.Contains("show=\"" + Uri.EscapeDataString("测试附件") + "\"", tag.Value);
            Assert.DoesNotContain("qqbot-cmd-enter", markdown);
        }
        else { Assert.Contains("测试附件", markdown); Assert.DoesNotContain("%2Ffile", markdown); }
    }

    [Fact]
    public async Task UnsupportedSchemesAndExternalCommandTagsNeverBecomeActions()
    {
        var markdown = Markdown(await CaptureAsync("会话 #abcdef123456\n[运行](javascript:alert(1))\n[外部动作](qqbot://anything)\n`<qqbot-cmd-enter text=\"go\" />`\n<qqbot-cmd-input text=\"go\" />"));
        Assert.DoesNotContain("javascript:", markdown); Assert.DoesNotContain("qqbot://", markdown);
        Assert.DoesNotContain("<qqbot-cmd-enter", markdown); Assert.DoesNotContain("text=\"go\"", Regex.Match(markdown, "<qqbot-cmd-input[^>]+>").Value);
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
    }

    [Fact]
    public async Task DecodedFileLabelsCannotIntroduceActionsOrAlterLiteralPercentText()
    {
        const string label = "&lt;qqbot-cmd-enter text=&quot;go&quot; /&gt; 100%20";
        var markdown = Markdown(await CaptureAsync($"会话 #abcdef123456\n[{label}](file:///D:/test.txt)"));
        Assert.DoesNotContain("<qqbot-cmd-enter", markdown);
        Assert.Contains("100%20", markdown);
        Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input "));
    }

    [Fact]
    public async Task SetextHeadingsKeepTheirUnderlineAttached()
    {
        var markdown = Markdown(await CaptureAsync("#abcdef123456\n\n标题\n---\n下一段"));
        Assert.Contains("标题\n---", markdown);
    }

    [Fact]
    public async Task ReferenceLinksBecomeInlineLinksAndQuotedFileActions()
    {
        const string text = "[发布 `报告😀`][r]\n[附件][f]\n[外部动作][bad]\n\n[r]: https://example.com/release?q=1&n=2#top\n[f]: <D:/文档/报告 (1).txt>\n[bad]: qqbot://anything";
        var markdown = Markdown(await CaptureAsync(text, true));
        Assert.Contains("[发布 `报告😀`](https://example.com/release?q=1&n=2#top)", markdown);
        Assert.DoesNotContain("[r]", markdown); Assert.DoesNotContain("[f]", markdown); Assert.DoesNotContain("D:/", markdown);
        var tag = Assert.Single(Regex.Matches(markdown, "<qqbot-cmd-input[^>]+reference=\"true\"[^>]*/>"));
        Assert.Contains("text=\"%2Ffile\"", tag.Value); Assert.Contains("show=\"" + Uri.EscapeDataString("附件") + "\"", tag.Value);
        Assert.DoesNotContain("qqbot://", markdown); Assert.DoesNotContain("qqbot-cmd-enter", markdown);
    }

    [Fact]
    public async Task ReferenceExpansionStillNeutralizesSourceCommandTags()
    {
        const string text = "[<qqbot-cmd-enter text=\"go\" />][r]\n\n[r]: file:///D:/test.txt";
        var body = await CaptureAsync(text, true);
        Assert.Equal(0, body.GetProperty("msg_type").GetInt32());
        var content = body.GetProperty("content").GetString()!;
        Assert.DoesNotContain("<qqbot-", content); Assert.DoesNotContain("[r]", content);
        Assert.Contains("＜qqbot-cmd-enter", content);
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

    private static async Task<JsonElement> CaptureAsync(string text, bool linkFiles = false)
    {
        JsonElement body = default;
        using var http = new HttpClient(new ScriptedHttp(async (request, _, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getAppAccessToken", StringComparison.Ordinal))
                return ScriptedHttp.Json("{\"access_token\":\"test-token\",\"expires_in\":7200}");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); body = json.RootElement.Clone();
            return ScriptedHttp.Json("{\"id\":\"sent\"}");
        }));
        await new QqClient(http, () => ("test-app", "test-secret")).SendTextAsync(new() { Id = "owner" }, text, CancellationToken.None, linkFiles: linkFiles);
        return body;
    }

    private static string Markdown(JsonElement body)
    {
        Assert.Equal(2, body.GetProperty("msg_type").GetInt32()); Assert.False(body.TryGetProperty("content", out _));
        return body.GetProperty("markdown").GetProperty("content").GetString()!;
    }

}
