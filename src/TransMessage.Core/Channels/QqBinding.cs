using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Channels;

public sealed record BindChallenge(string Code, string Kind, string Name, DateTimeOffset ExpiresAt);

public sealed class QqBinding(ConfigStore config)
{
    private readonly object _gate = new();
    private BindChallenge? _challenge;
    private int _attempts;
    public BindChallenge Create(string kind, string name)
    {
        if (kind is not ("c2c" or "group")) throw new ArgumentException("Invalid binding kind.", nameof(kind));
        lock (_gate)
        {
            _attempts = 0;
            return _challenge = new(RandomNumberGenerator.GetInt32(100000, 1000000).ToString(), kind,
                string.IsNullOrWhiteSpace(name) ? kind == "c2c" ? "我的 QQ" : "我的群聊" : name.Trim(), DateTimeOffset.UtcNow.AddMinutes(5));
        }
    }
    public QqTarget? Accept(string eventType, JsonElement data)
    {
        var kind = eventType switch { "C2C_MESSAGE_CREATE" => "c2c", "GROUP_AT_MESSAGE_CREATE" => "group", _ => "" };
        if (kind.Length == 0 || !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        var text = Regex.Replace(content.GetString()!, @"<@!?\d+>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
        if (!text.StartsWith("bind ", StringComparison.OrdinalIgnoreCase)) return null;
        lock (_gate)
        {
            if (_challenge is null || _challenge.Kind != kind || _challenge.ExpiresAt < DateTimeOffset.UtcNow || ++_attempts > 10) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(text[5..].Trim()), Encoding.UTF8.GetBytes(_challenge.Code))) return null;
            string? id = null;
            if (kind == "group" && data.TryGetProperty("group_openid", out var group)) id = group.GetString();
            if (kind == "c2c" && data.TryGetProperty("author", out var author) && author.TryGetProperty("user_openid", out var user)) id = user.GetString();
            if (string.IsNullOrWhiteSpace(id)) return null;
            var target = new QqTarget { Id = id, Kind = kind, Name = _challenge.Name };
            config.Update(c => { c.Qq.Targets.RemoveAll(t => t.Id == id && t.Kind == kind); c.Qq.Targets.Add(target); });
            _challenge = null;
            return target;
        }
    }
}
