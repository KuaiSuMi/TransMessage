using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Codex;

public sealed class CodexRpc : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process? _process;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<JsonElement> _messages = Channel.CreateBounded<JsonElement>(256);
    private readonly Task _reading;
    private int _next;
    private int _disposed;
    public ChannelReader<JsonElement> Messages => _messages.Reader;
    public Task Completion => _reading;
    public bool IsConnected => !_reading.IsCompleted;

    public CodexRpc(Stream output, Stream input, Process? process = null)
    {
        _reader = new(output, Encoding.UTF8); _writer = new(input, new UTF8Encoding(false)) { AutoFlush = true }; _process = process;
        _reading = Task.Run(ReadAsync);
    }
    public static string FindExecutable(string configured)
    {
        if (configured.Length > 0)
        {
            if (!File.Exists(configured)) throw new AppException("CODEX_EXE", "找不到配置的 Codex exe。");
            return configured;
        }
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktop))
        {
            var native = Directory.EnumerateFiles(desktop, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (native is not null) return native;
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        if (Directory.Exists(npm))
        {
            var native = Directory.EnumerateFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (native is not null) return native;
        }
        throw new AppException("CODEX_EXE", "请安装 Codex，或在设置中选择 codex.exe。");
    }
    public static async Task<CodexRpc> ConnectAsync(string executable, bool proxy, CancellationToken ct, string? codexHome = null)
    {
        var info = new ProcessStartInfo(FindExecutable(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        if (proxy) info.ArgumentList.Add("proxy");
        else { info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://"); }
        if (codexHome is not null) info.Environment["CODEX_HOME"] = codexHome;
        var process = Process.Start(info) ?? throw new AppException("CODEX_START", "无法启动 Codex 连接。");
        _ = Task.Run(async () => { try { var buffer = new char[2048]; while (await process.StandardError.ReadAsync(buffer) > 0) { } } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { } });
        var rpc = new CodexRpc(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process);
        try { await rpc.InitializeAsync(ct); return rpc; }
        catch { await rpc.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        await CallAsync("initialize", new { clientInfo = new { name = "transmessage", title = "TransMessage", version = BuildInfo.Version }, capabilities = new { experimentalApi = true } }, ct);
        await WriteAsync(new { method = "initialized", @params = new { } }, ct);
    }
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        var id = "tm-" + Interlocked.Increment(ref _next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsConnected) throw Disconnected();
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException) { throw new AppException("CODEX_TIMEOUT", "Codex 请求超时，请在电脑查看执行状态。"); }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task ReplyAsync(JsonElement id, object result, CancellationToken ct) => WriteAsync(new { id, result }, ct);
    public Task RejectAsync(JsonElement id, CancellationToken ct) => WriteAsync(new { id, error = new { code = -32601, message = "TransMessage does not handle this request; use the host client." } }, ct);
    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try { if (!IsConnected) throw Disconnected(); await _writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct); }
        catch (IOException) { throw Disconnected(); }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) throw new AppException("CODEX_MESSAGE_SIZE", "Codex 返回内容过大，已断开连接。");
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id) && _pending.TryRemove(id.ToString(), out var waiter))
                {
                    if (root.TryGetProperty("error", out var error))
                        waiter.TrySetException(new AppException("CODEX_RPC", "Codex 拒绝请求：" + Security.Sanitizer.Truncate(new Security.Sanitizer().Redact(CodexEvents.Text(error, "message")), 500)));
                    else if (root.TryGetProperty("result", out var result)) waiter.TrySetResult(result.Clone());
                    else waiter.TrySetException(Disconnected());
                }
                else if (root.TryGetProperty("method", out _) && !_messages.Writer.TryWrite(root.Clone())) throw new AppException("CODEX_BUSY", "Codex 事件过多，连接已停止，待回答问题已失效。");
            }
        }
        catch (Exception e) when (e is IOException or JsonException or AppException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(Disconnected());
            _pending.Clear(); _messages.Writer.TryComplete();
        }
    }
    private static AppException Disconnected() => new("CODEX_DISCONNECTED", "Codex 控制连接已断开，请在电脑重新连接。");
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        _reader.Dispose(); _writer.Dispose();
        if (_process is not null)
        {
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { if (!_process.HasExited) _process.Kill(true); }
            _process.Dispose();
        }
        await _reading; _stop.Dispose();
    }
}
