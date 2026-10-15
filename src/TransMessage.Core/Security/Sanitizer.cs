using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TransMessage.Core.Notifications;

namespace TransMessage.Core.Security;

public sealed class Sanitizer(Func<IEnumerable<string>>? knownSecrets = null)
{
    private static readonly Regex Secrets = new(
        @"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z]+ )*PRIVATE KEY-----|\z)|" +
        @"\b(?:sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_]{8,}|github_pat_[A-Za-z0-9_]{8,}|AKIA[A-Z0-9]{16}|ASIA[A-Z0-9]{16})\b|" +
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b|" +
        @"(?im)(?:authorization|cookie|set-cookie)\s*:\s*[^\r\n]+|" +
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+|" +
        "(?im)[\"']?\\b[A-Z0-9_]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)[A-Z0-9_]*[\"']?\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    public string Redact(string? input)
    {
        var value = input ?? "";
        foreach (var secret in knownSecrets?.Invoke() ?? [])
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        try { return Secrets.Replace(value, "[REDACTED]").Replace("\0", "", StringComparison.Ordinal).Trim(); }
        catch (RegexMatchTimeoutException) { return "[内容因脱敏超时而省略]"; }
    }
    public Notification Clean(Notification n) => n with
    {
        Title = Truncate(Redact(n.Title), 160),
        Message = Truncate(Redact(n.Message), 3000),
        Device = Redact(n.Device),
        Source = Redact(n.Source),
        Tags = n.Tags.Select(Redact).ToArray(),
        Metadata = n.Metadata.ToDictionary(p => p.Key, p => Redact(p.Value))
    };
    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        const string suffix = "…内容过长，已截断";
        var length = Math.Max(0, maxChars - suffix.Length);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + suffix;
    }
    public static string QqText(Notification n)
    {
        var timeLabel = n.Metadata.GetValueOrDefault("timestamp_source") == "received" ? "接收时间（来源未提供原始时间）" : "原始时间";
        var time = n.Timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var text = $"{timeLabel}：{time}（北京时间）\n{n.Title}\n\n{n.Message}\n\n设备：{n.Device}";
        // Conservative local budget; do not claim this is the platform's universal limit.
        if (Encoding.UTF8.GetByteCount(text) <= 4000) return text;
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 3950) break;
            builder.Append(rune); bytes += rune.Utf8SequenceLength;
        }
        return builder + "\n…内容过长，已截断";
    }
}
