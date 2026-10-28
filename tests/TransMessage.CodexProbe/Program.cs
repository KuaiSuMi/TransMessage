using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TransMessage.Core.Channels;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

try { await RunAsync(args); return 0; }
catch (Exception e) { Console.Error.WriteLine("Native Codex probe failed: " + e.Message); return 1; }

static async Task RunAsync(string[] args)
{
    // Only this process receives the isolated home; model responses and QQ receipts are local fixtures.
    var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/codex-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(root); var home = Path.Combine(root, "codex-home"); Directory.CreateDirectory(home);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var model = builder.Build(); var requests = new ConcurrentQueue<string>();
    var imageRequests = new ConcurrentDictionary<string, string>(); var completedThreads = new ConcurrentQueue<string>();
    var bothImageRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var bCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var requestNumber = 0; var imageRequestsPending = 0; var simultaneousImageRequests = false;
    model.MapPost("/v1/responses", async (HttpContext context) =>
    {
        using var reader = new StreamReader(context.Request.Body); var body = await reader.ReadToEndAsync(stop.Token);
        var number = Interlocked.Increment(ref requestNumber); requests.Enqueue(body);
        await File.WriteAllTextAsync(Path.Combine(root, $"model-request-{number}.json"), body, stop.Token);
        Console.WriteLine($"Scripted model request {number}");
        var imageKey = body.Contains("PROBE_IMAGE_A", StringComparison.Ordinal) ? "A" : body.Contains("PROBE_IMAGE_B", StringComparison.Ordinal) ? "B" : null;
        if (imageKey is not null)
        {
            Check(imageRequests.TryAdd(imageKey, body), "A draft unexpectedly started more than one model request.");
            if (Interlocked.Increment(ref imageRequestsPending) == 2) { simultaneousImageRequests = true; bothImageRequests.TrySetResult(true); }
            await bothImageRequests.Task.WaitAsync(stop.Token);
            // Wait for B's real completion event before allowing A to finish.
            if (imageKey == "A") await bCompleted.Task.WaitAsync(stop.Token);
        }
        object item = number == 1
            ? new { type = "function_call", call_id = "probe-question", name = "request_user_input", arguments = JsonSerializer.Serialize(new { questions = new[] { new { id = "scope", header = "范围", question = "采用哪种检查？", options = new[] { new { label = "快速", description = "最少检查" }, new { label = "完整", description = "全部检查" } } } } }) }
            : new { type = "message", role = "assistant", id = "probe-result-" + number, content = new[] { new { type = "output_text", text = imageKey is null ? "问答联调完成" : $"图文联调完成 {imageKey}" } } };
        var id = "probe-response-" + number;
        var events = new object[] { new { type = "response.created", response = new { id } }, new { type = "response.output_item.done", item }, new { type = "response.completed", response = new { id, usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } } } };
        context.Response.ContentType = "text/event-stream";
        foreach (var e in events) await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(e) + "\n\n", stop.Token);
        if (imageKey is not null) Interlocked.Decrement(ref imageRequestsPending);
    });
    await model.StartAsync(stop.Token);
    var address = model.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), $"""
model_provider = "probe"
approval_policy = "on-request"
sandbox_mode = "workspace-write"
[model_providers.probe]
name = "Local scripted probe"
base_url = "{address}/v1"
wire_api = "responses"
requires_openai_auth = false
""", new UTF8Encoding(false), stop.Token);
    var paths = new AppPaths(Path.Combine(root, "transmessage")); var config = new ConfigStore(paths);
    config.Update(c => { c.Qq.AppId = "probe"; c.Qq.Targets.Add(new() { Id = "probe-owner" }); c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "probe-owner"; });
    var references = new QqReferenceStore(paths); var notices = new ConcurrentQueue<(Notification Notice, string Reference)>();
    var receiptNumber = 0; CodexRpc? native = null;
    var images = new Dictionary<string, byte[]> { ["/a.png"] = Png(220, 30, 40), ["/b.png"] = Png(20, 60, 220) };
    using var http = new HttpClient(new ImageFixtureHandler(images)); using var cache = new QqImageCache(paths, http);
    await using var remote = new RemoteCodex(config, new(), (owner, notice, _, _) =>
    {
        var reference = "REFIDX_probe_" + Interlocked.Increment(ref receiptNumber) + "==";
        if (notice.Metadata.TryGetValue("thread_id", out var threadId))
        {
            references.Save(owner, reference, threadId, notice.Metadata.GetValueOrDefault("question_code"));
            if (notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal))
            {
                completedThreads.Enqueue(threadId);
                if (notice.Message.Contains("图文联调完成 B", StringComparison.Ordinal)) bCompleted.TrySetResult(true);
            }
        }
        notices.Enqueue((notice, reference)); Console.WriteLine(notice.Title + "\n" + notice.Message);
        return Task.FromResult(new DeliveryResult(true, notice.Id, "sent"));
    }, async (_, ct) => native = await CodexRpc.ConnectAsync(args.Length > 1 ? args[1] : "", false, ct, home), cache);
    await remote.ApplySettingsAsync(stop.Token);
    var catalog = await native!.CallAsync("model/list", new { limit = 100, includeHidden = false }, stop.Token);
    var selected = catalog.GetProperty("data").EnumerateArray().FirstOrDefault(m => m.TryGetProperty("inputModalities", out var kinds)
        && kinds.EnumerateArray().Any(k => k.GetString() == "image") && m.GetProperty("supportedReasoningEfforts").GetArrayLength() > 0);
    Check(selected.ValueKind == JsonValueKind.Object, "Native model/list did not expose an image model with reasoning settings.");
    var modelName = selected.GetProperty("model").GetString()!;
    var efforts = selected.GetProperty("supportedReasoningEfforts").EnumerateArray().Select(e => e.GetProperty("reasoningEffort").GetString()!).ToArray();
    var effort = efforts.Contains("low", StringComparer.Ordinal) ? "low" : efforts.First();
    Console.WriteLine($"Native catalog selected {modelName} / {effort}");
    var a = await remote.CreateManagedAsync(root, "隔离图文 A", stop.Token);
    var b = await remote.CreateManagedAsync(root, "隔离图文 B", stop.Token);
    await remote.ModelCommandAsync(a.Id, modelName, stop.Token);
    await remote.ReasoningCommandAsync(a.Id, effort, stop.Token);
    await remote.ContinueAsync(a.Id, "询问检查范围", stop.Token);
    await UntilAsync(() => remote.Pending.Count == 1 && notices.Any(n => n.Notice.Metadata.ContainsKey("question_code")), stop.Token);
    var pending = remote.Pending.Single();
    var question = notices.Single(n => n.Notice.Metadata.GetValueOrDefault("question_code") == pending.Code);
    Check(references.Find("probe-owner", question.Reference) == new QqReference(a.Id, pending.Code), "Question receipt lost its thread or question code.");
    var commandCount = 0;
    async Task SendAsync(string text, string? reference = null, string? image = null)
    {
        Check(++commandCount <= 12, "Probe exceeded the real command rate limit.");
        var attachments = image is null ? Array.Empty<object>() : [new { url = "https://probe-images.example" + image, content_type = "image/png", size = images[image].Length }];
        var payload = JsonSerializer.SerializeToElement(new
        {
            id = "probe-message-" + commandCount,
            author = new { user_openid = "probe-owner" },
            content = text,
            timestamp = DateTimeOffset.UtcNow,
            message_type = reference is null ? 0 : 103,
            message_scene = new { ext = reference is null ? [] : new[] { "msg_idx=CURRENT==", "ref_msg_idx=" + reference } },
            attachments,
            msg_elements = new[] { new { content = "QUOTED_OLD_TEXT_MUST_NOT_BE_SENT", attachments = new[] { new { url = "https://probe-images.example/quoted.png", content_type = "image/png" } } } }
        });
        var message = QqMessages.Parse("C2C_MESSAGE_CREATE", payload) ?? throw new InvalidOperationException("Official QQ input did not parse.");
        Check(message.ReferenceIndex == reference && message.Text == text && message.Images.Count == attachments.Length && message.InputError is null, "QQ quote parser mixed quoted content with the current reply.");
        await remote.ProcessAsync(message, stop.Token);
        await UntilAsync(() => notices.Any(n => n.Notice.Timestamp == message.Time), stop.Token);
        var reply = notices.Last(n => n.Notice.Timestamp == message.Time).Notice;
        Check(!reply.Message.EndsWith("]", StringComparison.Ordinal), "Remote command failed: " + reply.Message);
    }
    await SendAsync("2", question.Reference);
    await UntilAsync(() => notices.Any(n => n.Notice.Message.Contains("问答联调完成", StringComparison.Ordinal)), stop.Token);
    var answerReachedModel = requests.Skip(1).Any(body => body.Contains("完整", StringComparison.Ordinal) || body.Contains("\\u5B8C\\u6574", StringComparison.OrdinalIgnoreCase));
    Check(answerReachedModel && remote.Pending.Count == 0 && notices.Any(n => n.Notice.Message.Contains("确认处理", StringComparison.Ordinal)), "Quoted answer was not acknowledged by the original pending request.");
    var aReference = notices.Last(n => n.Notice.Metadata.GetValueOrDefault("thread_id") == a.Id && n.Notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal)).Reference;
    await SendAsync("/model " + modelName, aReference);
    await SendAsync($"#{b.Id} /model {modelName}");
    await remote.ReasoningCommandAsync(b.Id, effort, stop.Token);
    var bReference = notices.Last(n => n.Notice.Metadata.GetValueOrDefault("thread_id") == b.Id).Reference;
    await SendAsync("/d", aReference);
    await SendAsync("/draft", bReference);
    await SendAsync("", aReference, "/a.png");
    await SendAsync("PROBE_IMAGE_B 请检查蓝图", bReference);
    await SendAsync("PROBE_IMAGE_A 请检查红图", aReference);
    await SendAsync("", bReference, "/b.png");
    Check(requests.Count == 2, "An unsubmitted draft reached the model.");
    await SendAsync("/s", aReference);
    await SendAsync("/s", bReference);
    await UntilAsync(() => notices.Any(n => n.Notice.Message.Contains("图文联调完成 A", StringComparison.Ordinal))
        && notices.Any(n => n.Notice.Message.Contains("图文联调完成 B", StringComparison.Ordinal)), stop.Token);
    Check(simultaneousImageRequests && imageRequests.Count == 2 && requestNumber == 4, "Both native image turns were not pending simultaneously.");
    VerifyRequest(imageRequests["A"], "A", "B", modelName, effort, 220, 30, 40);
    VerifyRequest(imageRequests["B"], "B", "A", modelName, effort, 20, 60, 220);
    Check(completedThreads.SequenceEqual(new[] { a.Id, b.Id, a.Id }), "Native turn completion order or session association is incorrect.");
    Check(notices.Any(n => n.Notice.Metadata.GetValueOrDefault("thread_id") == a.Id && n.Notice.Message.Contains("图文联调完成 A", StringComparison.Ordinal))
        && notices.Any(n => n.Notice.Metadata.GetValueOrDefault("thread_id") == b.Id && n.Notice.Message.Contains("图文联调完成 B", StringComparison.Ordinal)), "Interleaved result notification lost its thread association.");
    var evidence = new
    {
        time = DateTimeOffset.Now,
        real_codex = true,
        isolated_home = true,
        scripted_model = true,
        real_qq = false,
        quote_message_type = 103,
        virtual_qq_receipt = true,
        quoted_answer_acknowledged = true,
        answer_reached_original_pending = true,
        quote_draft_short = true,
        quote_draft_long = true,
        separate_image_text = true,
        drafts_not_submitted_early = true,
        simultaneous_native_turns = true,
        reverse_completion_order = true,
        image_encoding_verified = true,
        session_isolation = true,
        selected_model = modelName,
        selected_effort = effort,
        next_turn_settings_verified = true,
        command_count = commandCount,
        model_requests = requestNumber,
        thread_ids = new[] { a.Id, b.Id }
    };
    await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), stop.Token);
    Console.WriteLine(JsonSerializer.Serialize(evidence));
    await model.StopAsync(stop.Token);
}

static byte[] Png(byte red, byte green, byte blue)
{
    var pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new[] { red, green, blue }).ToArray();
    var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3); source.Freeze();
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
    using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
}
static void VerifyRequest(string body, string own, string other, string model, string effort, byte red, byte green, byte blue)
{
    using var parsed = JsonDocument.Parse(body); var request = parsed.RootElement;
    Check(request.GetProperty("model").GetString() == model && request.GetProperty("reasoning").GetProperty("effort").GetString() == effort, "Next-turn model or reasoning setting did not reach the real model API.");
    Check(body.Contains("PROBE_IMAGE_" + own, StringComparison.Ordinal) && !body.Contains("PROBE_IMAGE_" + other, StringComparison.Ordinal)
        && !body.Contains("QUOTED_OLD_TEXT", StringComparison.Ordinal), "Draft text crossed sessions or included the quoted message.");
    var images = request.GetProperty("input").EnumerateArray().Where(i => i.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
        .SelectMany(i => i.GetProperty("content").EnumerateArray()).Where(i => i.TryGetProperty("type", out var t) && t.GetString() == "input_image")
        .Select(i => i.GetProperty("image_url").GetString()!).ToArray();
    Check(images.Length == 1 && images[0].StartsWith("data:image/", StringComparison.Ordinal) && images[0].Contains(";base64,", StringComparison.Ordinal), "Native Codex did not encode exactly one local image.");
    using var stream = new MemoryStream(Convert.FromBase64String(images[0][(images[0].IndexOf(',') + 1)..]));
    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
    var converted = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0); var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 3];
    converted.CopyPixels(pixels, converted.PixelWidth * 3, 0);
    Check(converted.PixelWidth == 16 && converted.PixelHeight == 16 && Math.Abs(pixels[0] - red) <= 10 && Math.Abs(pixels[1] - green) <= 10 && Math.Abs(pixels[2] - blue) <= 10, "Encoded image belongs to the other draft or was damaged.");
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task UntilAsync(Func<bool> condition, CancellationToken ct) { while (!condition()) await Task.Delay(50, ct); }

sealed class ImageFixtureHandler(IReadOnlyDictionary<string, byte[]> images) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Host: "probe-images.example" } uri || !images.TryGetValue(uri.AbsolutePath, out var bytes))
            throw new InvalidOperationException("Unexpected image download; quoted attachments must be ignored.");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("image/png"); return Task.FromResult(response);
    }
}
