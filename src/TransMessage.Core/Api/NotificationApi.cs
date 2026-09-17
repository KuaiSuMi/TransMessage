using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Api;

public sealed class NotificationApi(ServerSettings settings, SecretStore secrets, Func<Notification, CancellationToken, Task<DeliveryResult>> send)
{
    public string Status { get; private set; } = "未启动";
    public async Task RunAsync(CancellationToken ct)
    {
        _ = secrets.ApiToken();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(settings.AllowLan ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback, settings.Port);
            k.Limits.MaxRequestBodySize = 32768; k.Limits.MaxConcurrentConnections = 64;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("all",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
            var expected = Encoding.UTF8.GetBytes("Bearer " + secrets.ApiToken());
            var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { ok = false, error = "unauthorized" }); return; }
            await next(context);
        });
        app.MapPost("/v1/notify", async (HttpContext context) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (!context.Request.HasJsonContentType()) return Results.Json(new { ok = false, error = "application/json required" }, statusCode: 415);
            try
            {
                var notification = await JsonSerializer.DeserializeAsync<Notification>(context.Request.Body, Notification.Json, timeout.Token);
                if (notification is null) return Results.BadRequest(new { ok = false, error = "invalid_json" });
                notification.Validate();
                var result = await send(notification, timeout.Token);
                return Results.Json(new { ok = result.Ok, id = result.Id, status = result.Status, error = result.Error }, statusCode: result.Ok ? 200 : result.Status == "paused" ? 409 : 502);
            }
            catch (BadHttpRequestException e) when (e.StatusCode == 413) { return Results.StatusCode(413); }
            catch (JsonException) { return Results.BadRequest(new { ok = false, error = "invalid_json" }); }
            catch (AppException e) { return Results.BadRequest(new { ok = false, error = e.Code }); }
            catch (OperationCanceledException) { return Results.StatusCode(408); }
            catch (Exception) { return Results.Json(new { ok = false, error = "internal_error" }, statusCode: 500); }
        });
        try
        {
            await app.StartAsync(ct); Status = settings.AllowLan ? $"局域网 · 端口 {settings.Port} · 已启用认证" : $"仅本机 · 端口 {settings.Port}";
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await app.StopAsync(CancellationToken.None); Status = "已停止"; }
    }
}
