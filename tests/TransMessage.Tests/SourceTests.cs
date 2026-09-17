using System.Diagnostics;
using TransMessage.Core.Notifications;
using TransMessage.Core.Sources;

namespace TransMessage.Tests;

public sealed class SourceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RunnerPreservesOutputAndExit(int exit)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var result = await new CommandRunner().RunAsync(["cmd", "/d", "/c", $"echo hello & echo problem 1>&2 & exit /b {exit}"], output, error);
        Assert.Equal(exit, result.ExitCode); Assert.Contains("hello", output.ToString()); Assert.Contains("problem", error.ToString()); Assert.Contains("problem", result.Tail);
    }
    [Fact]
    public async Task RunnerHandlesUnicodeBatchAndSpacedArguments()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "测试 command.cmd"); await File.WriteAllTextAsync(path, "@echo off\r\necho %~1\r\nexit /b 3\r\n");
        var output = new StringWriter(); var result = await new CommandRunner().RunAsync([path, "two words"], output, new StringWriter());
        Assert.Equal(3, result.ExitCode); Assert.Contains("two words", output.ToString());
    }
    [Fact]
    public async Task RunnerCancellationTerminatesChild()
    {
        using var stop = new CancellationTokenSource(300);
        var clock = Stopwatch.StartNew(); var result = await new CommandRunner().RunAsync(["cmd", "/c", "ping -n 30 127.0.0.1 >nul"], new StringWriter(), new StringWriter(), ct: stop.Token);
        Assert.NotEqual(0, result.ExitCode); Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }
    [Theory]
    [InlineData(".crdownload")]
    [InlineData(".part")]
    [InlineData(".tmp")]
    public async Task DownloadRenameAndGrowingFileSendOnce(string temporary)
    {
        using var data = new TestData(); data.Config.Update(c => c.Downloads.StableSeconds = 2);
        var downloads = Path.Combine(data.Paths.Root, "下载"); Directory.CreateDirectory(downloads);
        var channel = new RecordingChannel(); var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        using var watcher = new StableFileWatcher(data.Config, dispatcher.SendAsync, TestData.Logger); using var stop = new CancellationTokenSource();
        var running = watcher.RunAsync([downloads], stop.Token);
        var file = Path.Combine(downloads, "example.zip"); await File.WriteAllTextAsync(file + temporary, "first");
        await Task.Delay(300); Assert.Empty(channel.Sent); File.Move(file + temporary, file);
        await Task.Delay(1000); await File.AppendAllTextAsync(file, "more data");
        await Task.Delay(1200); Assert.Empty(channel.Sent);
        await TestData.UntilAsync(() => channel.Sent.Count == 1);
        await Task.Delay(2300); Assert.Single(channel.Sent); stop.Cancel(); await running;
    }
    [Fact]
    public async Task WatchFileWaitsForExclusiveAccess()
    {
        using var data = new TestData(); data.Config.Update(c => c.Downloads.StableSeconds = 2); var file = Path.Combine(data.Paths.Root, "model.bin");
        var sent = new List<Notification>();
        using var stop = new CancellationTokenSource(10000); using var watcher = new StableFileWatcher(data.Config, (n, _) => { sent.Add(n); return Task.FromResult(new DeliveryResult(true, n.Id, "sent")); }, TestData.Logger);
        using (var handle = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var running = watcher.RunAsync([data.Paths.Root], stop.Token, file);
            await handle.WriteAsync(new byte[] { 1 }); await handle.FlushAsync(); await Task.Delay(2800); Assert.Empty(sent);
            handle.Dispose(); await running;
        }
        Assert.Single(sent);
    }
}
