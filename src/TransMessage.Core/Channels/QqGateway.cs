using System.Net.WebSockets;
using System.Text.Json;
using Serilog;

namespace TransMessage.Core.Channels;

public sealed class QqGateway(QqClient qq, QqBinding binding, ILogger logger)
{
    public string Status { get; private set; } = "未连接";
    public event Action? TargetBound;
    private string? _session;
    private long? _sequence;
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task RunAsync(CancellationToken ct, Func<CancellationToken, Task<Uri>>? gatewayResolver = null)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? heartbeat = null;
            try
            {
                Status = "正在连接";
                var token = await qq.GetTokenAsync(ct);
                var url = await (gatewayResolver?.Invoke(ct) ?? qq.GatewayAsync(ct));
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(url, connectTimeout.Token);
                var ack = true;
                var receiveTimeout = TimeSpan.FromSeconds(30);
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var receiveBudget = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                    receiveBudget.CancelAfter(receiveTimeout);
                    using var json = await ReceiveAsync(socket, receiveBudget.Token);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("op", out var op)) throw new AppException("QQ_GATEWAY_SCHEMA", "QQ 事件格式变化，请检查更新。");
                    if (root.TryGetProperty("s", out var seq) && seq.TryGetInt64(out var number)) _sequence = number;
                    var data = root.TryGetProperty("d", out var d) ? d : default;
                    switch (op.GetInt32())
                    {
                        case 10:
                            if (heartbeat is not null) throw new AppException("QQ_GATEWAY_SCHEMA", "重复的 QQ 握手事件。");
                            await SendAsync(socket, _session is null
                                ? new { op = 2, d = (object)new { token = "QQBot " + token, intents = 1 << 25, shard = new[] { 0, 1 } } }
                                : new { op = 6, d = (object)new { token = "QQBot " + token, session_id = _session, seq = _sequence } }, connection.Token);
                            var interval = data.GetProperty("heartbeat_interval").GetInt32();
                            if (interval is < 1000 or > 300000) throw new AppException("QQ_HEARTBEAT", "QQ 心跳间隔无效。");
                            receiveTimeout = TimeSpan.FromMilliseconds(interval * 2 + 5000);
                            heartbeat = Task.Run(async () =>
                            {
                                try
                                {
                                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                                    while (await timer.WaitForNextTickAsync(connection.Token))
                                    {
                                        if (!Volatile.Read(ref ack)) { socket.Abort(); return; }
                                        Volatile.Write(ref ack, false);
                                        await SendAsync(socket, new { op = 1, d = _sequence }, connection.Token);
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (WebSocketException) { socket.Abort(); }
                            }, connection.Token);
                            break;
                        case 11: Volatile.Write(ref ack, true); break;
                        case 1: await SendAsync(socket, new { op = 1, d = _sequence }, connection.Token); break;
                        case 7: throw new AppException("QQ_RECONNECT", "QQ 请求重新连接。");
                        case 9:
                            if (data.ValueKind != JsonValueKind.True) { _session = null; _sequence = null; qq.InvalidateToken(); }
                            throw new AppException("QQ_SESSION", "QQ 会话需要重建。");
                        case 0:
                            var type = root.GetProperty("t").GetString()!;
                            if (type is "READY" or "RESUMED")
                            {
                                if (type == "READY") _session = data.GetProperty("session_id").GetString();
                                Status = "已连接"; failures = 0;
                            }
                            var target = binding.Accept(type, data);
                            if (target is not null)
                            {
                                TargetBound?.Invoke();
                                if (data.TryGetProperty("id", out var id))
                                {
                                    try { await qq.SendTextAsync(target, "TransMessage 已绑定。回到电脑点击“发送测试通知”验证主动消息权限。", ct, id.GetString()); }
                                    catch (Exception e) { logger.Warning("Binding reply failed: {Kind}", e.GetType().Name); }
                                }
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Status = AppException.Describe(e);
                logger.Warning("QQ gateway disconnected: {Kind}", e is AppException app ? app.Code : e.GetType().Name);
                var close = (int?)socket.CloseStatus;
                if (close is 4004 or 4006 or 4007 or 4009) { _session = null; _sequence = null; qq.InvalidateToken(); }
                if (close is 4914 or 4915)
                {
                    Status = "QQ 事件权限不足，请在开放平台开启群和私聊事件，然后重新验证配置。";
                    return;
                }
            }
            finally
            {
                await connection.CancelAsync();
                socket.Abort();
                if (heartbeat is not null) { try { await heartbeat; } catch (OperationCanceledException) { } }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))) + Random.Shared.NextDouble()), ct); }
            catch (OperationCanceledException) { break; }
        }
        Status = "已停止";
    }

    private async Task SendAsync(ClientWebSocket socket, object message, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new AppException("QQ_WS_CLOSED", "QQ 事件连接已断开，正在重连。");
            if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > 1024 * 1024)
                throw new AppException("QQ_WS_SIZE", "QQ 事件格式或大小异常。");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(data.ToArray());
    }
}
