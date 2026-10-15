using System.Security.Cryptography;
using System.Text.Json;
using TransMessage.Core.Configuration;
using TransMessage.Core.Security;

namespace TransMessage.Core.Notifications;

public sealed record HistoryEntry(string Key, DateTimeOffset Time, Notification Notification, string Status, string? Error, string Route);

public sealed class NotificationHistory(AppPaths paths, Sanitizer sanitizer)
{
    private string FileName => Path.Combine(paths.Root, "history.dpapi");
    public IReadOnlyList<HistoryEntry> Read() => AtomicFile.Locked(FileName, () => ReadCore().Where(x => x.Time >= DateTimeOffset.UtcNow.AddDays(-30)).OrderByDescending(x => x.Time).Take(500).ToArray());
    public string Begin(Notification notification, string route = "通知目标")
    {
        var entry = new HistoryEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, sanitizer.Clean(notification), "sending", null, route);
        Change(items => items.Add(entry)); return entry.Key;
    }
    public void Complete(string key, string status, string? error = null) => Change(items =>
    {
        var index = items.FindIndex(x => x.Key == key);
        if (index >= 0) items[index] = items[index] with { Status = status, Error = error is null ? null : Sanitizer.Truncate(sanitizer.Redact(error), 500) };
    });
    public void Clear() => Change(items => items.Clear());
    private List<HistoryEntry> ReadCore()
    {
        if (!File.Exists(FileName)) return [];
        if (new FileInfo(FileName).Length > 16 * 1024 * 1024) throw new AppException("HISTORY_SIZE", "通知历史异常过大，请备份后清理。");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(FileName), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(clear) ?? throw new JsonException(); }
        catch (JsonException) { throw new AppException("HISTORY_INVALID", "通知历史无法读取，原文件未覆盖。"); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private void Change(Action<List<HistoryEntry>> edit) => AtomicFile.Locked(FileName, () =>
    {
        var items = ReadCore(); edit(items);
        items = items.Where(x => x.Time >= DateTimeOffset.UtcNow.AddDays(-30)).OrderBy(x => x.Time).TakeLast(500).ToList();
        var clear = JsonSerializer.SerializeToUtf8Bytes(items);
        try { AtomicFile.WriteBytes(FileName, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        return true;
    });
    public static string Label(string status) => status switch
    {
        "sent" => "已发送",
        "failed" => "发送失败",
        "uncertain" => "送达不确定",
        "sending" => "发送中 / 中断后结果未知",
        "paused" => "已暂停",
        _ => status
    };
}
