using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TransMessage.Core;
using TransMessage.Core.Api;
using TransMessage.Core.Channels;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Tests;

public sealed class NetworkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiValidatesAuthJsonSizeAndOrigin(bool lan)
    {
        using var data = new TestData(); var secrets = new SecretStore(data.Paths); var token = secrets.ApiToken();
        var sent = new RecordingChannel(); var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), sent, TestData.Logger);
        var settings = new ServerSettings { Port = TestData.Port(), AllowLan = lan }; var api = new NotificationApi(settings, secrets, dispatcher.SendAsync);
        using var stop = new CancellationTokenSource(); var running = api.RunAsync(stop.Token);
        await TestData.UntilAsync(() => api.Status.Contains("端口", StringComparison.Ordinal));
        using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{settings.Port}") };
        using var invalid = await http.PostAsJsonAsync("/v1/notify", new { title = "test" }); Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var valid = await http.PostAsJsonAsync("/v1/notify", new { title = "test", message = "password=my-password", level = "success" }); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("[REDACTED]", sent.Sent.Single().Message);
        using var malformed = await http.PostAsync("/v1/notify", new StringContent("{no", Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var badLevel = await http.PostAsJsonAsync("/v1/notify", new { level = "no" }); Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
        using var huge = await http.PostAsync("/v1/notify", new StringContent(new string('x', 50000), Encoding.UTF8, "application/json")); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var browser = await http.PostAsJsonAsync("/v1/notify", new { title = "blocked" }); Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin"); secrets.RegenerateApiToken();
        using var rotated = await http.PostAsJsonAsync("/v1/notify", new { title = "test" }); Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
        stop.Cancel(); await running;
    }
    [Fact]
    public async Task QqHttpAuthC2cGroupAndTokenSingleFlight()
    {
        await using var server = new TestServer(); var authCount = 0; var scopes = new List<string>();
        server.App.MapPost("/app/getAppAccessToken", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Equal("app", body.RootElement.GetProperty("appId").GetString()); Assert.Equal("secret", body.RootElement.GetProperty("clientSecret").GetString());
            Interlocked.Increment(ref authCount); return Results.Json(new { access_token = "token", expires_in = 7200 });
        });
        server.App.MapPost("/v2/{scope}/{id}/messages", async (HttpContext context, string scope, string id) =>
        {
            Assert.Equal("QQBot token", context.Request.Headers.Authorization.ToString());
            using var body = await JsonDocument.ParseAsync(context.Request.Body); Assert.Equal("hello", body.RootElement.GetProperty("content").GetString());
            Assert.False(body.RootElement.TryGetProperty("msg_id", out _)); scopes.Add(scope); return Results.Json(new { id = "sent-" + id });
        });
        await server.StartAsync(); using var http = new HttpClient(); var qq = new QqClient(http, () => ("app", "secret"), apiBase: server.Address, tokenBase: server.Address);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => qq.GetTokenAsync())); Assert.Equal(1, authCount);
        await qq.SendTextAsync(new() { Id = "user", Kind = "c2c" }, "hello", CancellationToken.None);
        await qq.SendTextAsync(new() { Id = "group", Kind = "group" }, "hello", CancellationToken.None);
        Assert.Equal(new[] { "users", "groups" }, scopes);
    }
    [Theory]
    [InlineData(401, true)]
    [InlineData(429, true)]
    [InlineData(500, false)]
    [InlineData(403, false)]
    public async Task QqRefreshRetryAndAmbiguousFailure(int status, bool expectSuccess)
    {
        var auth = 0; var sends = 0;
        using var handler = new ScriptedHttp((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("getAppAccessToken", StringComparison.Ordinal)) { auth++; return Task.FromResult(ScriptedHttp.Json("{\"access_token\":\"token\",\"expires_in\":7200}")); }
            sends++;
            var response = sends == 1 ? ScriptedHttp.Json("{\"code\":123}", (HttpStatusCode)status) : ScriptedHttp.Json("{\"id\":\"sent\"}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10)); return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); var qq = new QqClient(http, () => ("app", "secret"));
        var action = () => qq.SendTextAsync(new() { Id = "user" }, "hello", CancellationToken.None);
        if (expectSuccess) await action(); else await Assert.ThrowsAsync<AppException>(action);
        Assert.Equal(expectSuccess ? 2 : 1, sends); Assert.Equal(status == 401 ? 2 : 1, auth);
    }
    [Fact]
    public async Task QqAuthFailureAndTimeoutDoNotLeak()
    {
        using var handler = new ScriptedHttp((_, _, _) => Task.FromResult(ScriptedHttp.Json("{\"message\":\"secret-value\"}", HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler); var qq = new QqClient(http, () => ("app", "secret-value"));
        var error = await Assert.ThrowsAsync<AppException>(() => qq.GetTokenAsync()); Assert.DoesNotContain("secret-value", error.Message);
        using var interrupted = new ScriptedHttp((request, _, _) => request.RequestUri!.AbsolutePath.Contains("getAppAccessToken", StringComparison.Ordinal)
            ? Task.FromResult(ScriptedHttp.Json("{\"access_token\":\"token\",\"expires_in\":7200}")) : throw new TaskCanceledException());
        using var timed = new HttpClient(interrupted); var client = new QqClient(timed, () => ("app", "secret"));
        var uncertain = await Assert.ThrowsAsync<AppException>(() => client.SendTextAsync(new() { Id = "user" }, "hello", CancellationToken.None)); Assert.Equal("QQ_DELIVERY_UNCERTAIN", uncertain.Code); Assert.Equal(2, interrupted.Requests);
    }
    [Fact]
    public async Task QqWebSocketIdentifiesHeartbeatsAndBindsOnce()
    {
        using var data = new TestData(); var binding = new QqBinding(data.Config); var challenge = binding.Create("c2c", "手机");
        var identified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwarded = new TaskCompletionSource<TransMessage.Core.Codex.QqPrivateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bindingReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string bindingTime = "2026-10-15T12:01:02.3456789+08:00";
        await using var server = new TestServer(); server.App.UseWebSockets();
        server.App.MapPost("/app/getAppAccessToken", () => Results.Json(new { access_token = "token", expires_in = 7200 }));
        server.App.MapPost("/v2/users/{id}/messages", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body); bindingReply.TrySetResult(body.RootElement.GetProperty("content").GetString()!); return Results.Json(new { id = "reply" });
        });
        server.App.Map("/gateway-test", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            async Task Send(object item) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(item).AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
            await Send(new { op = 10, d = new { heartbeat_interval = 1000 } }); var buffer = new byte[4096];
            var first = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted); using var identify = JsonDocument.Parse(buffer.AsMemory(0, first.Count));
            Assert.Equal(2, identify.RootElement.GetProperty("op").GetInt32()); Assert.Equal(1 << 25, identify.RootElement.GetProperty("d").GetProperty("intents").GetInt32()); identified.TrySetResult();
            await Send(new { op = 0, t = "READY", s = 1, d = new { session_id = "session" } });
            var ev = new { op = 0, t = "C2C_MESSAGE_CREATE", s = 2, d = new { id = "incoming", content = "bind " + challenge.Code, author = new { user_openid = "bound-user" }, timestamp = bindingTime } };
            await Send(ev); await Send(ev);
            await Send(new { op = 0, t = "C2C_MESSAGE_CREATE", s = 3, d = new { id = "command", content = "任务", author = new { user_openid = "bound-user" }, timestamp = DateTimeOffset.UtcNow } });
            try
            {
                var next = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted); using var beat = JsonDocument.Parse(buffer.AsMemory(0, next.Count));
                Assert.Equal(1, beat.RootElement.GetProperty("op").GetInt32()); heartbeat.TrySetResult(); await Send(new { op = 11, d = (object?)null });
                await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        });
        await server.StartAsync(); using var http = new HttpClient(); var qq = new QqClient(http, () => ("app", "secret"), apiBase: server.Address, tokenBase: server.Address);
        var gateway = new QqGateway(qq, binding, TestData.Logger, (type, payload) =>
        {
            var message = TransMessage.Core.Codex.RemoteCodex.ParseMessage(type, payload); if (message is { Text: "任务" }) forwarded.TrySetResult(message); return message is not null;
        }); using var stop = new CancellationTokenSource();
        var url = new UriBuilder(server.Address) { Scheme = "ws", Path = "/gateway-test" }.Uri;
        var running = gateway.RunAsync(stop.Token, _ => Task.FromResult(url));
        await identified.Task.WaitAsync(TimeSpan.FromSeconds(5)); await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forwardedMessage = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("bound-user", forwardedMessage.Sender); Assert.Equal("任务", forwardedMessage.Text);
        Assert.StartsWith("原始时间：" + bindingTime, await bindingReply.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(data.Config.Load().Qq.Targets); Assert.Equal("bound-user", data.Config.Load().Qq.Targets[0].Id); stop.Cancel(); await running;
    }
    [Fact]
    public void BindingWrongKindAndWrongCodeCannotBind()
    {
        using var data = new TestData(); var binding = new QqBinding(data.Config); var challenge = binding.Create("group", "群");
        using var wrong = JsonDocument.Parse("{\"content\":\"bind 000000\",\"group_openid\":\"g\"}"); Assert.Null(binding.Accept("GROUP_AT_MESSAGE_CREATE", wrong.RootElement));
        using var correct = JsonDocument.Parse(JsonSerializer.Serialize(new { content = "<@123> bind " + challenge.Code, group_openid = "g" }));
        Assert.Null(binding.Accept("C2C_MESSAGE_CREATE", correct.RootElement)); Assert.NotNull(binding.Accept("GROUP_AT_MESSAGE_CREATE", correct.RootElement)); Assert.Null(binding.Accept("GROUP_AT_MESSAGE_CREATE", correct.RootElement));
    }
}
