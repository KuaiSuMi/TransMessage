using System.Security.Cryptography;
using System.Text.Json;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Channels;

public sealed record QqReference(string ThreadId, string? QuestionCode, string? TurnId = null, DateTimeOffset? Timestamp = null);

public sealed class QqReferenceStore(AppPaths paths)
{
    private sealed record Entry(string Owner, string Index, string ThreadId, string? QuestionCode, DateTimeOffset Time, string? TurnId = null, DateTimeOffset? Timestamp = null);
    private string FileName => Path.Combine(paths.Root, "qq-references.dpapi");

    public void Save(string owner, string refIndex, string threadId, string? questionCode = null, string? turnId = null, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256 || string.IsNullOrWhiteSpace(refIndex) || refIndex.Length > 2048
            || !Guid.TryParse(threadId, out _) || questionCode?.Length > 64 || turnId?.Length > 128)
            throw new AppException("QQ_REFERENCE_INVALID", "QQ 引用关联信息无效。");
        AtomicFile.Locked(FileName, () =>
        {
            var entries = ReadCore();
            entries.RemoveAll(x => x.Owner == owner && x.Index == refIndex);
            entries.Add(new(owner, refIndex, threadId, questionCode, DateTimeOffset.UtcNow, turnId, timestamp));
            var clear = JsonSerializer.SerializeToUtf8Bytes(entries.OrderBy(x => x.Time).TakeLast(2000));
            try { AtomicFile.WriteBytes(FileName, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            return true;
        });
    }

    public QqReference? Find(string owner, string refIndex) => AtomicFile.Locked(FileName, () =>
    {
        var entry = ReadCore().LastOrDefault(x => x.Owner == owner && x.Index == refIndex);
        return entry is null ? null : new QqReference(entry.ThreadId, entry.QuestionCode, entry.TurnId, entry.Timestamp);
    });

    private List<Entry> ReadCore()
    {
        if (!File.Exists(FileName)) return [];
        if (new FileInfo(FileName).Length > 8 * 1024 * 1024) throw new AppException("QQ_REFERENCE_SIZE", "QQ 引用索引异常过大，原文件未修改。");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(FileName), null, DataProtectionScope.CurrentUser);
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(clear) ?? throw new JsonException();
            return entries.Where(x => x.Time >= DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(2000).ToList();
        }
        catch (JsonException) { throw new AppException("QQ_REFERENCE_INVALID", "QQ 引用索引无法读取，原文件未修改。"); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
