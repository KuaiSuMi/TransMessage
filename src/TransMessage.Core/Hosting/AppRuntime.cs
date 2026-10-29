using Serilog;
using TransMessage.Core.Api;
using TransMessage.Core.Channels;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;
using TransMessage.Core.Sources;
using TransMessage.Core.Update;

namespace TransMessage.Core.Hosting;

public sealed class AppRuntime : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public ConfigStore Config { get; }
    public SecretStore Secrets { get; }
    public ILogger Log { get; }
    public InstancePipe Pipe { get; }
    public QqClient Qq { get; }
    public QqBinding Binding { get; }
    public Dispatcher Dispatcher { get; }
    public Sanitizer Sanitizer { get; }
    public ReleaseUpdater Updater { get; }
    public RemoteCodex Remote { get; }
    public QqGateway? Gateway { get; private set; }
    public NotificationApi? Api { get; private set; }
    public StableFileWatcher? Files { get; private set; }
    public CodexSessionWatcher? Codex { get; private set; }
    public Dictionary<string, string> Errors { get; } = [];
    public event Action? ShowRequested;
    public event Action? StopRequested;
    private readonly HttpClient _http;
    private readonly QqReferenceStore _qqReferences;
    private CancellationTokenSource? _services;
    private readonly List<Task> _tasks = [];
    public AppRuntime(AppPaths paths, INotificationChannel? channel = null)
    {
        Paths = paths; Config = new(paths); _ = Config.Load(); Secrets = new(paths); Pipe = new(paths);
        Log = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(paths.Logs, "transmessage-.log"),
            rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 2 * 1024 * 1024, rollOnFileSizeLimit: true,
            retainedFileCountLimit: 14, retainedFileTimeLimit: TimeSpan.FromDays(14), shared: true).CreateLogger();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        Qq = new(_http, () => (Config.Load().Qq.AppId, Secrets.Get("qq") ?? ""), Config.Load().Qq.Sandbox);
        Binding = new(Config);
        _qqReferences = new(paths);
        Sanitizer = new(() => new[] { Secrets.Get("qq") ?? "", Secrets.Get("api") ?? "" });
        Dispatcher = new(Config, new(paths), Sanitizer, channel ?? new QqOfficialChannel(Config, Qq, RememberReferenceAsync), Log);
        Updater = new(_http, paths);
        Remote = new(Config, Sanitizer, SendPrivateAsync, sendFile: SendPrivateFileAsync);
    }
    private async Task<DeliveryResult> SendPrivateAsync(string owner, Notification notification, string? replyId, CancellationToken ct)
    {
        var c = Config.Load();
        if (!c.RemoteCodex.Enabled || c.RemoteCodex.OwnerId != owner || !c.Qq.Targets.Any(t => t.Id == owner && t.Kind == "c2c" && t.Enabled))
            return new(false, notification.Id, "failed", "私聊控制授权已撤销。");
        var clean = Sanitizer.Clean(notification);
        if (notification.Metadata.ContainsKey("reply_part")) clean = clean with { Message = Security.Sanitizer.Truncate(Sanitizer.Redact(notification.Message, trim: false), 3000) };
        var key = Dispatcher.History.Begin(clean, "控制者私聊");
        DeliveryResult result;
        try
        {
            var target = new QqTarget { Id = owner, Kind = "c2c" };
            var linkFiles = Guid.TryParse(clean.Metadata.GetValueOrDefault("thread_id"), out _) && !string.IsNullOrWhiteSpace(clean.Metadata.GetValueOrDefault("turn_id"));
            var receipt = await Qq.SendTextAsync(target, Security.Sanitizer.QqText(clean), ct, replyId, linkFiles);
            await RememberReferenceAsync(target, clean, receipt); result = new(true, clean.Id, "sent");
        }
        catch (Exception e) { result = new(false, clean.Id, e is AppException error && (error.Code.StartsWith("QQ_4", StringComparison.Ordinal) || error.Code is "QQ_NOT_CONFIGURED" or "QQ_REPLY_LIMIT") ? "failed" : "uncertain", AppException.Describe(e)); }
        Dispatcher.History.Complete(key, result.Status, result.Error); return result;
    }
    private Task RememberReferenceAsync(QqTarget target, Notification notification, QqSendReceipt receipt)
    {
        if (target.Kind == "c2c" && receipt.ReferenceIndex is { Length: > 0 } index
            && notification.Metadata.TryGetValue("thread_id", out var thread) && Guid.TryParse(thread, out _))
            _qqReferences.Save(target.Id, index, thread, notification.Metadata.GetValueOrDefault("question_code"), notification.Metadata.GetValueOrDefault("turn_id"),
                notification.Metadata.GetValueOrDefault("timestamp_source") == "original" ? notification.Timestamp : null);
        return Task.CompletedTask;
    }
    private async Task<DeliveryResult> SendPrivateFileAsync(string owner, string path, Notification notification, string replyId, CancellationToken ct)
    {
        var c = Config.Load();
        if (!c.RemoteCodex.Enabled || c.RemoteCodex.OwnerId != owner || !c.Qq.Targets.Any(t => t.Id == owner && t.Kind == "c2c" && t.Enabled))
            return new(false, notification.Id, "failed", "请先连接已绑定的本人 QQ。");
        var clean = Sanitizer.Clean(notification); var key = Dispatcher.History.Begin(clean, "控制者私聊附件");
        DeliveryResult result;
        try
        {
            var target = new QqTarget { Id = owner, Kind = "c2c" };
            var receipt = await Qq.SendFileAsync(target, path, ct, replyId);
            await RememberReferenceAsync(target, clean, receipt); result = new(true, clean.Id, "sent");
        }
        catch (Exception e)
        {
            var failed = e is AppException error && (error.Code.StartsWith("QQ_4", StringComparison.Ordinal)
                || error.Code.StartsWith("QQ_FILE_", StringComparison.Ordinal) || error.Code is "QQ_REPLY_LIMIT" or "QQ_NOT_CONFIGURED")
                || e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException;
            result = new(false, clean.Id, failed ? "failed" : "uncertain", AppException.Describe(e));
        }
        Dispatcher.History.Complete(key, result.Status, result.Error); return result;
    }
    public async Task<DeliveryResult> SendAsync(Notification notification, CancellationToken ct = default)
    {
        var reply = await Pipe.TrySendAsync(new("notify", notification), ct);
        return reply?.Delivery ?? (reply is null ? await Dispatcher.SendAsync(notification, ct) : new(false, notification.Id, "failed", reply.Message));
    }
    public async Task StartServicesAsync(CancellationToken ct)
    {
        await StopServicesAsync();
        _services = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _services.Token;
        var c = Config.Load();
        Qq.SetSandbox(c.Qq.Sandbox); Qq.InvalidateToken();
        lock (Errors) Errors.Clear();
        if (!string.IsNullOrWhiteSpace(c.Qq.AppId) && Secrets.Get("qq") is not null)
        {
            Gateway = new(Qq, Binding, Log, Remote.Enqueue); Track("QQ", () => Gateway.RunAsync(token));
        }
        try { await Remote.ApplySettingsAsync(ct); }
        catch (Exception e) { lock (Errors) Errors["QQ 控制"] = AppException.Describe(e); }
        if (c.Server.Enabled) { Api = new(c.Server, Secrets, Dispatcher.SendAsync); Track("API", () => Api.RunAsync(token)); }
        if (c.Downloads.Enabled)
        {
            var directories = c.Downloads.Directories.ToList();
            if (c.Downloads.AutoDownloads) { try { directories.Add(StableFileWatcher.DownloadsPath()); } catch (AppException e) { lock (Errors) Errors["Downloads"] = e.Message; } }
            Files = new(Config, Dispatcher.SendAsync, Log); Track("Downloads", () => Files.RunAsync(directories, token));
        }
        if (c.Codex.Enabled && c.Codex.SessionWatcher)
        { Codex = new(Config, Dispatcher.SendAsync, Log); Track("Codex", () => Codex.RunAsync(token)); }
        if (c.Update.Check) Track("Update", async () => { await Updater.CheckAsync(BuildInfo.Version, ct: token); });
    }
    private void Track(string name, Func<Task> run) => _tasks.Add(Task.Run(async () =>
    {
        try { await run(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { lock (Errors) Errors[name] = AppException.Describe(e); Log.Warning("Background component failed: {Name} {Kind}", name, e.GetType().Name); }
    }));
    public Task RunPipeAsync(CancellationToken ct) => Pipe.RunAsync(async (request, token) =>
    {
        switch (request.Action)
        {
            case "notify" when request.Notification is not null:
                var result = await Dispatcher.SendAsync(request.Notification, token); return new(result.Ok, result.Error ?? result.Status, result);
            case "show": ShowRequested?.Invoke(); return new(true, "窗口已唤起");
            case "status": return new(true, $"后台运行中；QQ：{Gateway?.Status ?? "未配置"}；API：{Api?.Status ?? "关闭"}；下载：{Files?.Status ?? "关闭"}；Codex：{Codex?.Status ?? "关闭"}");
            case "reload": await StartServicesAsync(ct); return new(true, "后台设置已应用");
            case "bind":
                var challenge = Binding.Create(request.Kind ?? "c2c", request.Name ?? ""); return new(true, challenge.Code);
            case "stop": return new(true, "正在退出");
            default: return new(false, "不支持的本地操作");
        }
    }, ct, request => { if (request.Action == "stop") StopRequested?.Invoke(); });
    public async Task StopServicesAsync()
    {
        if (_services is null) return;
        await _services.CancelAsync();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        _tasks.Clear(); Codex?.Dispose(); Files?.Dispose(); _services.Dispose(); _services = null;
        Gateway = null; Api = null; Files = null; Codex = null;
    }
    public async ValueTask DisposeAsync()
    {
        await Remote.DisposeAsync(); await StopServicesAsync(); _http.Dispose(); (Log as IDisposable)?.Dispose();
    }
}
