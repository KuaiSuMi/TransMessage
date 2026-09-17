using System.IO.Pipes;
using System.Text.Json;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Core.Hosting;

public sealed record PipeRequest(string Action, Notification? Notification = null, string? Kind = null, string? Name = null);
public sealed record PipeReply(bool Ok, string Message, DeliveryResult? Delivery = null);

public sealed class InstancePipe(AppPaths paths)
{
    private string Name => "TransMessage-" + paths.InstanceId;
    public async Task<PipeReply?> TrySendAsync(PipeRequest request, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(300, ct); }
        catch (TimeoutException) { return null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await WriteAsync(pipe, request, timeout.Token);
        return await ReadAsync<PipeReply>(pipe, timeout.Token);
    }
    public async Task RunAsync(Func<PipeRequest, CancellationToken, Task<PipeReply>> handler, CancellationToken ct, Action<PipeRequest>? completed = null)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                PipeReply result;
                PipeRequest? request = null;
                try { request = await ReadAsync<PipeRequest>(pipe, timeout.Token); result = await handler(request, timeout.Token); }
                catch (Exception e) { result = new(false, AppException.Describe(e)); }
                await WriteAsync(pipe, result, timeout.Token);
                if (request is not null) completed?.Invoke(request);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        }
    }
    private static async Task WriteAsync<T>(Stream stream, T item, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, Notification.Json);
        if (bytes.Length > 65536) throw new AppException("PIPE_SIZE", "本地请求超过大小限制。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > 65536) throw new AppException("PIPE_SIZE", "本地请求大小无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Notification.Json) ?? throw new JsonException();
    }
}
