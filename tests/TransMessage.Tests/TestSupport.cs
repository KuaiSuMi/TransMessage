using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class TestData : IDisposable
{
    public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "TransMessage-tests", Guid.NewGuid().ToString("N")));
    public ConfigStore Config { get; }
    public TestData() { Config = new(Paths); Config.Save(new()); }
    public void Dispose() { try { Directory.Delete(Paths.Root, true); } catch (IOException) { } }
    public static Serilog.ILogger Logger => new LoggerConfiguration().CreateLogger();
    public static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 6000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
public sealed class RecordingChannel : INotificationChannel
{
    public ConcurrentQueue<Notification> Sent { get; } = new();
    public Task SendAsync(Notification notification, CancellationToken cancellationToken) { Sent.Enqueue(notification); return Task.CompletedTask; }
}
public sealed class ScriptedHttp(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _requests;
    public int Requests => _requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, Interlocked.Increment(ref _requests), cancellationToken);
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
public sealed class TestServer : IAsyncDisposable
{
    public WebApplication App { get; }
    public Uri Address { get; private set; } = null!;
    public TestServer()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); App = builder.Build();
    }
    public async Task StartAsync()
    {
        await App.StartAsync(); var server = App.Services.GetRequiredService<IServer>();
        Address = new(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");
    }
    public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
}
