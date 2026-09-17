using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using TransMessage.Core.Configuration;
using TransMessage.Core.Security;

namespace TransMessage.Core.Notifications;

public sealed class DedupStore(AppPaths paths)
{
    private string PathName => Path.Combine(paths.Root, "dedup.json");
    public bool Reserve(string id) => AtomicFile.Locked(PathName, () =>
    {
        Dictionary<string, DateTimeOffset> entries;
        try { entries = File.Exists(PathName) ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(PathName))! : []; }
        catch (JsonException) { throw new AppException("DEDUP_CORRUPT", "去重记录损坏，已停止发送以防重复。请备份并移走 dedup.json 后重试。"); }
        if (entries is null) throw new AppException("DEDUP_CORRUPT", "去重记录无效，已停止发送。");
        var now = DateTimeOffset.UtcNow;
        entries = entries.Where(p => now - p.Value < TimeSpan.FromDays(7)).ToDictionary();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        if (entries.ContainsKey(key)) return false;
        if (entries.Count >= 50000) throw new AppException("DEDUP_FULL", "通知去重容量已满，请减少事件频率并检查日志。");
        entries[key] = now;
        AtomicFile.Write(PathName, JsonSerializer.Serialize(entries));
        return true;
    });
}

public sealed class Dispatcher(ConfigStore config, DedupStore dedup, Sanitizer sanitizer, INotificationChannel channel, ILogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public DeliveryResult? LastResult { get; private set; }
    public async Task<DeliveryResult> SendAsync(Notification notification, CancellationToken ct = default)
    {
        notification.Validate();
        var clean = sanitizer.Clean(notification);
        await _gate.WaitAsync(ct);
        try
        {
            if (config.Load().Notifications.Paused) return new(false, notification.Id, "paused", "通知已暂停。");
            if (!dedup.Reserve(notification.Id)) return new(true, notification.Id, "duplicate");
            try
            {
                await channel.SendAsync(clean, ct);
                LastResult = new(true, notification.Id, "sent");
                logger.Information("Notification sent");
            }
            catch (Exception e)
            {
                var message = AppException.Describe(e);
                logger.Warning("Notification delivery failed: {Code}", e is AppException app ? app.Code : e.GetType().Name);
                LastResult = new(false, notification.Id, "failed", message);
            }
            AtomicFile.Write(Path.Combine(config.Paths.Root, "last-delivery.json"), JsonSerializer.Serialize(
                new { time = DateTimeOffset.UtcNow, LastResult.Ok, LastResult.Status, LastResult.Error }));
            return LastResult;
        }
        finally { _gate.Release(); }
    }
}
