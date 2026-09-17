using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransMessage.Core.Notifications;

public sealed record Notification
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Title { get; init; } = "通知";
    public string Message { get; init; } = "";
    public string Level { get; init; } = "info";
    public string Source { get; init; } = "manual";
    public string Device { get; init; } = Environment.MachineName;
    public string[] Tags { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 256 || string.IsNullOrWhiteSpace(Title) || Title.Length > 300
            || Message is null || Message.Length > 16000 || Source is null || Source.Length > 100
            || Device is null || Device.Length > 256 || Tags is null || Tags.Length > 20
            || Tags.Any(t => t is null || t.Length > 100) || Metadata is null || Metadata.Count > 20
            || Metadata.Any(p => p.Key.Length > 100 || p.Value is null || p.Value.Length > 2000))
            throw new AppException("NOTIFICATION_INVALID", "通知内容无效或超过长度限制。");
        if (Level is not ("info" or "success" or "warning" or "error"))
            throw new AppException("LEVEL_INVALID", "level 必须为 info、success、warning 或 error。");
    }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };
}

public record DeliveryResult(bool Ok, string Id, string Status, string? Error = null);
public interface INotificationChannel
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}
public static class MessageTemplate
{
    public static string Expand(string template, params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values) template = template.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return template.Replace("{device}", Environment.MachineName, StringComparison.Ordinal);
    }
}
