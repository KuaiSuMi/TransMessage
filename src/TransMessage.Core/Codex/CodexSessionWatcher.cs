using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Serilog;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Core.Codex;

public sealed class CodexSessionWatcher(ConfigStore config, Func<Notification, CancellationToken, Task<DeliveryResult>> send, ILogger logger, string? home = null) : IDisposable
{
    private sealed class Cursor
    {
        public long Offset;
        public CodexTurnReader Reader { get; } = new();
        public List<byte> Pending { get; } = [];
        public bool Oversized;
    }
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(1);
    private FileSystemWatcher? _watcher;
    private readonly Security.Sanitizer _sanitizer = new(() => new[] { new Security.SecretStore(config.Paths).Get("qq") ?? "", new Security.SecretStore(config.Paths).Get("api") ?? "" });
    public string Status { get; private set; } = "未启动";
    public async Task RunAsync(CancellationToken ct)
    {
        var directory = Path.Combine(home ?? AppPaths.CodexHome, "sessions");
        if (!Directory.Exists(directory)) { Status = "未发现 Codex sessions 目录；运行一次 Codex 后重启后台。"; return; }
        _watcher = new(directory, "*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += OnChange; _watcher.Created += OnChange; _watcher.Renamed += OnChange;
        _watcher.Error += (_, _) => { Status = "会话事件缓冲溢出，正在重新扫描"; Rescan(directory); };
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
            await ReadAsync(file, false, ct);
        _watcher.EnableRaisingEvents = true;
        // Reconcile appends that raced with the startup scan, without replaying history.
        Rescan(directory);
        Status = _cursors.Values.Any(c => c.Reader.Identity is { IsUser: true }) ? "已监听本地用户会话" : "当前会话格式无法可靠识别；未知事件不发送";
        try
        {
            await foreach (var signal in _signal.Reader.ReadAllAsync(ct))
            {
                await Task.Delay(150, ct);
                foreach (var path in _dirty.Keys)
                    if (_dirty.TryRemove(path, out _)) await ReadAsync(path, true, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private void Rescan(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
            _dirty.TryAdd(path, 0);
        _signal.Writer.TryWrite(true);
    }
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (_dirty.Count < 1000) _dirty.TryAdd(e.FullPath, 0);
        _signal.Writer.TryWrite(true);
    }
    private async Task ReadAsync(string path, bool emit, CancellationToken ct)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!_cursors.TryGetValue(path, out var cursor))
            {
                if (_cursors.Count >= 500) _cursors.Remove(_cursors.Keys.First());
                cursor = new Cursor(); _cursors[path] = cursor;
                // Existing huge sessions: read metadata and a bounded tail, never the entire history.
                if (file.Length > 4 * 1024 * 1024)
                {
                    using var header = new StreamReader(file, Encoding.UTF8, true, 4096, true);
                    var first = CodexEvents.ReadBoundedLine(header, 1024 * 1024);
                    if (first is not null) cursor.Reader.ReadLine(first, config.Load());
                    cursor.Offset = file.Length - 4 * 1024 * 1024; cursor.Oversized = true;
                }
            }
            if (file.Length < cursor.Offset) { _cursors.Remove(path); return; }
            file.Position = cursor.Offset;
            var buffer = new byte[16384];
            int read;
            var current = config.Load();
            while ((read = await file.ReadAsync(buffer, ct)) > 0)
            {
                cursor.Offset += read;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == 10)
                    {
                        if (!cursor.Oversized)
                        {
                            var notification = cursor.Reader.ReadLine(Encoding.UTF8.GetString(cursor.Pending.ToArray()), current, _sanitizer);
                            if (emit && notification is not null) await send(notification, ct);
                        }
                        cursor.Pending.Clear(); cursor.Oversized = false;
                    }
                    else if (!cursor.Oversized)
                    {
                        if (cursor.Pending.Count < 2 * 1024 * 1024) cursor.Pending.Add(buffer[i]);
                        else { cursor.Pending.Clear(); cursor.Oversized = true; }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { logger.Debug("Codex file unavailable: {Kind}", e.GetType().Name); }
    }
    public void Dispose() => _watcher?.Dispose();
}
