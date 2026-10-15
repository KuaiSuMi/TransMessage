using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Channels;

public sealed class QqClient(HttpClient http, Func<(string AppId, string Secret)> credentials, bool sandbox = false,
    Uri? apiBase = null, Uri? tokenBase = null)
{
    private Uri _api = apiBase ?? new(sandbox ? "https://sandbox.api.sgroup.qq.com/" : "https://api.sgroup.qq.com/");
    private readonly Uri _tokenBase = tokenBase ?? new("https://bots.qq.com/");
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private string? _appId;
    private DateTimeOffset _refreshAt;
    public string Status { get; private set; } = "未认证";

    public void InvalidateToken() => _refreshAt = DateTimeOffset.MinValue;
    public void SetSandbox(bool enabled)
    {
        if (apiBase is null) _api = new(enabled ? "https://sandbox.api.sgroup.qq.com/" : "https://api.sgroup.qq.com/");
    }
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            var (appId, secret) = credentials();
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
                throw new AppException("QQ_NOT_CONFIGURED", "先在 QQ 机器人设置中填写 AppID 和 AppSecret。");
            if (_token is not null && _appId == appId && DateTimeOffset.UtcNow < _refreshAt) return _token;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.PostAsJsonAsync(new Uri(_tokenBase, "app/getAppAccessToken"),
                new { appId, clientSecret = secret }, timeout.Token);
            if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, 0);
            using var document = await ReadJsonAsync(response, timeout.Token);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(token.GetString()))
                throw new AppException("QQ_AUTH_SCHEMA", "QQ 未返回有效 Token。核对 AppID/AppSecret 和机器人状态。");
            var seconds = root.TryGetProperty("expires_in", out var expires) && int.TryParse(expires.ToString(), out var lifetime) ? lifetime : 0;
            if (seconds <= 0) throw new AppException("QQ_SCHEMA", "QQ 认证响应格式变化，请检查更新。");
            _token = token.GetString()!; _appId = appId;
            _refreshAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, seconds - Math.Min(300, seconds / 5)));
            Status = "认证成功";
            return _token;
        }
        catch (Exception e) { Status = AppException.Describe(e); throw; }
        finally { _tokenGate.Release(); }
    }

    public async Task<Uri> GatewayAsync(CancellationToken ct)
    {
        using var result = await RequestAsync(HttpMethod.Get, "gateway", null, ct);
        if (!result.RootElement.TryGetProperty("url", out var property) || !Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != "wss") throw new AppException("QQ_GATEWAY_SCHEMA", "QQ 没有返回安全的事件连接地址。");
        return uri;
    }

    public async Task SendTextAsync(QqTarget target, string text, CancellationToken ct, string? replyId = null)
    {
        var scope = target.Kind == "c2c" ? "users" : "groups";
        var body = new Dictionary<string, object> { ["content"] = text, ["msg_type"] = 0 };
        if (replyId is not null)
        {
            body["msg_id"] = replyId;
            body["msg_seq"] = 1;
        }
        using var result = await RequestAsync(HttpMethod.Post, $"v2/{scope}/{Uri.EscapeDataString(target.Id)}/messages", body, ct);
        if (!result.RootElement.TryGetProperty("id", out _) && !result.RootElement.TryGetProperty("message_id", out _))
            throw new AppException("QQ_SEND_SCHEMA", "QQ 发送响应缺少消息标识；结果不确定，未自动重发。");
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var token = await GetTokenAsync(ct);
            using var request = new HttpRequestMessage(method, new Uri(_api, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            request.Headers.UserAgent.ParseAdd("TransMessage/" + BuildInfo.Version);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, timeout.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new AppException("QQ_DELIVERY_UNCERTAIN", "QQ 网络请求中断，发送结果不确定。检查网络；为避免重复，未自动重发。");
            }
            using (response)
            {
                if (response.IsSuccessStatusCode) { Status = "最近发送成功"; return await ReadJsonAsync(response, ct); }
                var code = await ReadCodeAsync(response, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < 2) { InvalidateToken(); continue; }
                // Only retry explicit rejection. Ambiguous 5xx POSTs may already have been delivered.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2
                    || method == HttpMethod.Get && (int)response.StatusCode >= 500 && attempt < 2)
                {
                    var retry = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromMilliseconds((1 << attempt) * 1000 + Random.Shared.Next(100, 500));
                    if (retry > TimeSpan.FromSeconds(30)) throw Error(response.StatusCode, code);
                    await Task.Delay(retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                var error = Error(response.StatusCode, code); Status = error.Message; throw error;
            }
        }
        throw new AppException("QQ_RETRY_LIMIT", "QQ 请求重试次数已用完，请稍后检查诊断。");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var content = await response.Content.ReadAsByteArrayAsync(ct);
            if (content.Length > 1024 * 1024) throw new JsonException();
            return JsonDocument.Parse(content);
        }
        catch (JsonException) { throw new AppException("QQ_SCHEMA", "QQ 响应格式异常，请检查更新。"); }
    }
    private static async Task<int> ReadCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = await ReadJsonAsync(response, ct);
            return json.RootElement.TryGetProperty("code", out var code) && int.TryParse(code.ToString(), out var number) ? number : 0;
        }
        catch (AppException) { return 0; }
    }
    public static AppException Error(HttpStatusCode status, int code) => new($"QQ_{(int)status}_{code}", status switch
    {
        HttpStatusCode.Unauthorized => "QQ 认证失败。核对 AppID/AppSecret，必要时在开放平台重置凭证。",
        HttpStatusCode.Forbidden => "QQ 拒绝发送。检查沙箱成员、IP 白名单、事件权限、主动消息权限及当前会话授权。",
        HttpStatusCode.NotFound => "QQ 目标不可用。确认机器人仍在群内或好友列表，重新绑定目标。",
        HttpStatusCode.TooManyRequests => "QQ 发送频率或配额已受限，请稍后重试并检查开放平台额度。",
        _ when (int)status >= 500 => "QQ 服务暂时异常；发送结果可能不确定，已停止自动重发。",
        _ => "QQ 请求被拒绝。检查目标会话、主动消息额度及机器人权限；向开放平台查询此错误码。"
    });
}

public sealed class QqOfficialChannel(ConfigStore config, QqClient qq) : INotificationChannel
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        var targets = config.Load().Qq.Targets.Where(t => t.Enabled).ToArray();
        if (targets.Length == 0) throw new AppException("QQ_NO_TARGET", "尚未绑定通知目标。先在 QQ 设置中绑定私聊或群聊。");
        List<string> errors = [];
        foreach (var target in targets)
        {
            try { await qq.SendTextAsync(target, Sanitizer.QqText(notification), cancellationToken); }
            catch (Exception e) { errors.Add(AppException.Describe(e)); }
        }
        if (errors.Count > 0) throw new AppException("QQ_PARTIAL", $"{errors.Count}/{targets.Length} 个目标发送失败。{errors[0]}");
    }
}
