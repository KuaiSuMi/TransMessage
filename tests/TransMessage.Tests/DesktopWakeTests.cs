using System.ComponentModel;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class DesktopWakeTests
{
    private const string ThreadId = "11111111-2222-4333-8444-555555555555";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopQueueIsWrittenOnceBeforeWakeAndWakeFailureNeverResubmits(bool failWake)
    {
        using var data = new TestData(); await using var server = await UnifiedSessionTests.SessionServer.StartAsync();
        server.Pages = [[new { id = ThreadId, name = "Desktop 测试", source = "vscode", status = new { type = "notLoaded" } }]];
        var wakes = new List<string>();
        await using var remote = Create(data, server, (id, _) =>
        {
            Assert.Single(server.Calls, c => CodexEvents.Text(c, "method") == "thread/queue/add");
            wakes.Add(id);
            return failWake ? Task.FromException(new Win32Exception("Fixture protocol unavailable")) : Task.CompletedTask;
        });
        await remote.ApplySettingsAsync();
        var result = await remote.ContinueAsync(ThreadId[^12..], "继续原会话");
        Assert.Equal(ThreadId, Assert.Single(wakes));
        Assert.Contains(failWake ? "消息已加入队列。Desktop 唤醒失败" : "Desktop 后台唤醒请求", result);
        if (failWake) Assert.Contains("Desktop 唤醒失败：", remote.Status);
        Assert.Contains(ThreadId[^12..], result);
        Assert.Single(server.Calls, c => CodexEvents.Text(c, "method") == "thread/queue/add");
        Assert.DoesNotContain(server.Calls, c => CodexEvents.Text(c, "method") is "thread/resume" or "thread/queue/start" or "turn/start");
    }

    [Fact]
    public async Task RejectedQueueNeverRequestsDesktopWake()
    {
        using var data = new TestData(); await using var server = await UnifiedSessionTests.SessionServer.StartAsync();
        server.Pages = [[new { id = ThreadId, name = "Desktop 测试", source = "vscode", status = new { type = "notLoaded" } }]];
        server.RejectMethod = "thread/queue/add"; var wakes = 0;
        await using var remote = Create(data, server, (_, _) => { wakes++; return Task.CompletedTask; });
        await remote.ApplySettingsAsync();
        Assert.Equal("CODEX_RPC", (await Assert.ThrowsAsync<AppException>(() => remote.ContinueAsync(ThreadId, "不应唤醒"))).Code);
        Assert.Equal(0, wakes);
    }

    private static RemoteCodex Create(TestData data, UnifiedSessionTests.SessionServer server, Func<string, CancellationToken, Task> wake)
    {
        data.Config.Update(c => { c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "owner"; c.Qq.Targets.Add(new() { Id = "owner" }); });
        return new(data.Config, new(), (_, n, _, _) => Task.FromResult(new DeliveryResult(true, n.Id, "sent")),
            async (_, ct) => { await server.Client.InitializeAsync(ct); return server.Client; }, wakeDesktop: wake);
    }
}
