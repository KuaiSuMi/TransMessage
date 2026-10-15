using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class HistoryTests
{
    [Fact]
    public async Task HistoryEncryptsRedactsAndDeduplicatesDelivery()
    {
        using var data = new TestData(); var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), new RecordingChannel(), TestData.Logger);
        var notice = new Notification { Id = "history-1", Message = "visible-result password=private-value", Timestamp = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(1234567) };
        await dispatcher.SendAsync(notice); await dispatcher.SendAsync(notice);
        var history = new NotificationHistory(data.Paths, new()); var record = Assert.Single(history.Read());
        Assert.Equal("sent", record.Status); Assert.DoesNotContain("private-value", record.Notification.Message);
        Assert.Equal(notice.Timestamp.ToString("O"), record.Notification.Timestamp.ToString("O"));
        var bytes = File.ReadAllBytes(Path.Combine(data.Paths.Root, "history.dpapi"));
        Assert.DoesNotContain("visible-result", Encoding.UTF8.GetString(bytes));
        history.Clear(); Assert.Empty(history.Read());
    }
    [Fact]
    public void HistoryRetentionAndInterruptedStatusAreExplicit()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "history.dpapi");
        var records = Enumerable.Range(0, 502).Select(i => new HistoryEntry(i.ToString(), DateTimeOffset.UtcNow.AddMinutes(-i), new() { Message = "retained" }, "sent", null, "test")).ToList();
        records.Add(new("expired", DateTimeOffset.UtcNow.AddDays(-31), new(), "sent", null, "test"));
        File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(records), null, DataProtectionScope.CurrentUser));
        var history = new NotificationHistory(data.Paths, new()); Assert.Equal(500, history.Read().Count);
        history.Begin(new() { Title = "interrupted" });
        Assert.Equal(500, history.Read().Count); Assert.Equal("sending", history.Read()[0].Status); Assert.DoesNotContain(history.Read(), x => x.Key == "expired");
    }
    [Fact]
    public void CorruptHistoryIsNeverOverwritten()
    {
        using var data = new TestData(); var path = Path.Combine(data.Paths.Root, "history.dpapi"); File.WriteAllText(path, "broken");
        Assert.Throws<CryptographicException>(() => new NotificationHistory(data.Paths, new()).Begin(new())); Assert.Equal("broken", File.ReadAllText(path));
    }
    [Fact]
    public async Task UncertainDeliveryRemainsUncertainWithoutAutomaticResend()
    {
        using var data = new TestData(); var channel = new FailingChannel(); var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        var notice = new Notification { Id = "uncertain" }; Assert.Equal("uncertain", (await dispatcher.SendAsync(notice)).Status);
        Assert.Equal("duplicate", (await dispatcher.SendAsync(notice)).Status); Assert.Equal(1, channel.Calls);
        Assert.Equal("uncertain", dispatcher.History.Read().Single().Status);
    }
    private sealed class FailingChannel : INotificationChannel
    {
        public int Calls { get; private set; }
        public Task SendAsync(Notification notification, CancellationToken cancellationToken) { Calls++; throw new IOException("Connection lost after send"); }
    }
}
