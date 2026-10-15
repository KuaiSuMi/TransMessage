using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

try { await RunAsync(args); return 0; }
catch (Exception e) { Console.Error.WriteLine("Native Codex probe failed: " + e.Message); return 1; }

static async Task RunAsync(string[] args)
{
    // Real Codex process, isolated home, local scripted model, and no real QQ or paid inference.
    var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/codex-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(root); var home = Path.Combine(root, "codex-home"); Directory.CreateDirectory(home);
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var model = builder.Build(); var requests = new ConcurrentQueue<string>();
    model.MapPost("/v1/responses", async (HttpContext context) =>
    {
        using var reader = new StreamReader(context.Request.Body); requests.Enqueue(await reader.ReadToEndAsync());
        var number = requests.Count; Console.WriteLine($"Scripted model request {number}"); var id = "probe-response-" + number;
        object item = number == 1
            ? new { type = "function_call", call_id = "probe-question", name = "request_user_input", arguments = JsonSerializer.Serialize(new { questions = new[] { new { id = "scope", header = "范围", question = "采用哪种检查？", options = new[] { new { label = "快速", description = "最少检查" }, new { label = "完整", description = "全部检查" } } } } }) }
            : new { type = "message", role = "assistant", id = "probe-result", content = new[] { new { type = "output_text", text = "问答联调完成" } } };
        var events = new object[] { new { type = "response.created", response = new { id } }, new { type = "response.output_item.done", item }, new { type = "response.completed", response = new { id, usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } } } };
        context.Response.ContentType = "text/event-stream";
        foreach (var e in events) await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(e) + "\n\n");
    });
    await model.StartAsync();
    var address = model.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), $"""
model = "mock-model"
model_provider = "probe"
approval_policy = "on-request"
sandbox_mode = "workspace-write"
[model_providers.probe]
name = "Local scripted probe"
base_url = "{address}/v1"
wire_api = "responses"
requires_openai_auth = false
""", new UTF8Encoding(false));
    var config = new ConfigStore(new AppPaths(Path.Combine(root, "transmessage")));
    config.Update(c => { c.Qq.AppId = "probe"; c.Qq.Targets.Add(new() { Id = "probe-owner" }); c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "probe-owner"; c.RemoteCodex.Mode = "managed"; });
    var notices = new ConcurrentQueue<Notification>();
    await using var remote = new RemoteCodex(config, new(), (_, n, _, _) => { notices.Enqueue(n); Console.WriteLine(n.Title + "\n" + n.Message); return Task.FromResult(new DeliveryResult(true, n.Id, "sent")); },
        (_, ct) => CodexRpc.ConnectAsync(args.Length > 1 ? args[1] : "", false, ct, home));
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await remote.ApplySettingsAsync(stop.Token);
    var thread = await remote.CreateManagedAsync(root, "隔离问答联调", stop.Token);
    await remote.ProcessAsync(new("probe-continue", "probe-owner", $"继续 {thread.Id} 询问检查范围", DateTimeOffset.UtcNow), stop.Token);
    await UntilAsync(() => remote.Pending.Count == 1, stop.Token);
    var pending = remote.Pending.Single();
    await remote.ProcessAsync(new("probe-answer", "probe-owner", $"回答 {pending.Code} 2", DateTimeOffset.UtcNow), stop.Token);
    await UntilAsync(() => notices.Any(n => n.Message.Contains("问答联调完成", StringComparison.Ordinal)), stop.Token);
    var answerReachedModel = requests.Skip(1).Any(body => body.Contains("完整", StringComparison.Ordinal) || body.Contains("\\u5B8C\\u6574", StringComparison.OrdinalIgnoreCase));
    if (!answerReachedModel || remote.Pending.Count != 0 || !notices.Any(n => n.Message.Contains("确认处理", StringComparison.Ordinal))) throw new InvalidOperationException("Answer/ack/result verification failed.");
    var evidence = new { time = DateTimeOffset.Now, thread.Id, real_codex_process = true, isolated_home = true, scripted_model = true, real_qq = false, question_received = true, answer_acknowledged = true, answer_reached_model = true, turn_completed = true, model_requests = requests.Count };
    await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(evidence));
    await model.StopAsync();
}
static async Task UntilAsync(Func<bool> condition, CancellationToken ct) { while (!condition()) await Task.Delay(50, ct); }
