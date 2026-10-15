using System.Globalization;
using System.Text.Json;
using TransMessage.Core.Codex;
using TransMessage.Core.Security;

namespace TransMessage.Tests;

public sealed class TaskNameTests
{
    [Theory]
    [InlineData("允许软件内更新", "允许软件内更新")]
    [InlineData("1. 增加结束按钮\n2. 修改确认界面", "增加结束按钮")]
    [InlineData("## **修复** `登录` 提示。完成后测试。", "修复 登录 提示")]
    [InlineData("调整   房主\t标识", "调整 房主 标识")]
    [InlineData("查看 [更新说明](https://example.com/releases)", "查看 更新说明")]
    [InlineData("# Files mentioned by the user:\n## screenshot.png: C:/Temp/screenshot.png\nImage attachment: true\n## My request:\n简化任务名称\n<image>attachment</image>", "简化任务名称")]
    [InlineData("# Files pasted by the user:\n## requirements.txt\n## My request:\n实现通知历史", "实现通知历史")]
    [InlineData("# 我的需求：\n修复多会话漏报", "修复多会话漏报")]
    [InlineData("# Files mentioned by the user:\n## screenshot.png: C:/Temp/screenshot.png", "处理附件")]
    [InlineData("<external_codex_apps_open_page>{\"page_id\":null}</external_codex_apps_open_page>", "Codex 任务")]
    [InlineData("<send_user_message_question_reply>invalid-json</send_user_message_question_reply>", "补充任务信息")]
    [InlineData("", "Codex 任务")]
    public void NamesContainTheRequestInsteadOfEnvelopeOrFormatting(string input, string expected) => Assert.Equal(expected, TaskName.Short(input));

    [Fact]
    public void QuestionReplyUsesReadableQuestionAndDoesNotExposeAnswers()
    {
        var reply = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new[] { new { questionItemId = "internal-call-id", question = "导出文件使用哪种格式？", answer = "private-answer" } }) + "</send_user_message_question_reply>";
        Assert.Equal("确认：导出文件使用哪种格式", TaskName.Short(reply));
    }

    [Fact]
    public void NamesStayWithin32CharactersWithoutBreakingEmojiOrLeakingSecrets()
    {
        var shortName = TaskName.Short(string.Concat(Enumerable.Repeat("修复👨‍👩‍👧‍👦", 30)));
        Assert.Equal(32, new StringInfo(shortName).LengthInTextElements); Assert.EndsWith("…", shortName);
        Assert.DoesNotContain("内容过长", shortName); Assert.DoesNotContain("�", shortName);
        var secret = "private-credential"; var sanitizer = new Sanitizer(() => [secret]);
        Assert.Equal("检查 [REDACTED] 配置", TaskName.Short("检查 " + secret + " 配置", sanitizer));
        Assert.Equal("检查 [REDACTED]", TaskName.Short("检查 sk-**proj-abcdefghijk_987654321**"));
    }

    [Fact]
    public void HookAndSessionCompletionUseTheSameShortTaskName()
    {
        const string input = "# Files mentioned by the user:\n## screenshot.png: C:/Temp/screenshot.png\n## My request:\n修复确认按钮\n保留现有界面";
        var config = new TransMessage.Core.Configuration.AppConfig();
        var hook = CodexEvents.ParseNotify(CodexTests.Payload(input), config, new(CodexTests.ThreadId, @"C:\Project", "Codex Desktop", true))!;
        var reader = new CodexTurnReader(); reader.ReadLine(CodexTests.Meta(), config);
        reader.ReadLine(CodexTests.Event("event_msg", new { type = "task_started", turn_id = "turn-1" }), config);
        reader.ReadLine(CodexTests.Event("event_msg", new { type = "user_message", message = input }), config);
        var completion = reader.ReadLine(CodexTests.Event("event_msg", new { type = "task_complete", turn_id = "turn-1" }), config)!;
        foreach (var notice in new[] { hook, completion })
        {
            Assert.Contains("\n任务：修复确认按钮\n", notice.Message);
            Assert.DoesNotContain("screenshot", notice.Message); Assert.Equal($"codex:{CodexTests.ThreadId}:turn-1", notice.Id);
        }
    }
}
