using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Serilog;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Core.Sources;

public sealed record FileStamp(long Length, long ModifiedTicks, long CreatedTicks)
{
    public static FileStamp? Read(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? new(f.Length, f.LastWriteTimeUtc.Ticks, f.CreationTimeUtc.Ticks) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public sealed class StableFileWatcher(ConfigStore config, Func<Notification, CancellationToken, Task<DeliveryResult>> send, ILogger logger) : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FileStamp Stamp, DateTimeOffset Since)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(1);
    public string Status { get; private set; } = "未启动";
    public static bool IsTemporary(string path) => new[] { ".crdownload", ".part", ".tmp" }.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public async Task RunAsync(IEnumerable<string> directories, CancellationToken ct, string? onlyFile = null)
    {
        var roots = directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { missing++; continue; }
            foreach (var path in Directory.EnumerateFiles(root)) if (FileStamp.Read(path) is { } stamp) _observed[path] = stamp;
            var watcher = new FileSystemWatcher(root)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            void Change(object? _, FileSystemEventArgs e)
            {
                if (onlyFile is null || string.Equals(e.FullPath, onlyFile, StringComparison.OrdinalIgnoreCase))
                { _dirty[e.FullPath] = 0; _signal.Writer.TryWrite(true); }
            }
            watcher.Created += Change; watcher.Changed += Change; watcher.Deleted += Change; watcher.Renamed += (_, e) => { Change(null, e); _dirty[e.OldFullPath] = 0; };
            watcher.Error += (_, e) =>
            {
                logger.Warning("File watcher failed: {Kind}", e.GetException().GetType().Name);
                if (e.GetException() is not InternalBufferOverflowException)
                { Status = "部分目录监听已停止；恢复目录后重新应用文件监控设置。"; return; }
                try { foreach (var path in Directory.EnumerateFiles(root)) _dirty[path] = 0; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Status = "目录扫描失败；确认目录可访问后重新应用文件监控设置。";
                    logger.Warning("File rescan failed: {Kind}", error.GetType().Name);
                }
                _signal.Writer.TryWrite(true);
            };
            watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
        }
        Status = $"正在监控 {_watchers.Count} 个目录" + (missing > 0 ? $"；{missing} 个目录不存在" : "");
        if (onlyFile is not null) { _dirty[onlyFile] = 0; _signal.Writer.TryWrite(true); }
        if (_watchers.Count == 0) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_pending.Count == 0) await _signal.Reader.ReadAsync(ct);
                else await Task.Delay(500, ct);
                while (_signal.Reader.TryRead(out _)) { }
                foreach (var path in _dirty.Keys)
                {
                    if (!_dirty.TryRemove(path, out _)) continue;
                    if (IsTemporary(path)) continue;
                    var stamp = FileStamp.Read(path);
                    if (stamp is null) { _pending.Remove(path); _observed.Remove(path); continue; }
                    if (onlyFile is null && _observed.TryGetValue(path, out var old) && old == stamp) continue;
                    if (!_pending.TryGetValue(path, out var candidate) || candidate.Stamp != stamp) _pending[path] = (stamp, DateTimeOffset.UtcNow);
                }
                foreach (var (path, candidate) in _pending.ToArray())
                {
                    var stamp = FileStamp.Read(path);
                    if (stamp is null) { _pending.Remove(path); continue; }
                    if (stamp != candidate.Stamp) { _pending[path] = (stamp, DateTimeOffset.UtcNow); continue; }
                    if (DateTimeOffset.UtcNow - candidate.Since < TimeSpan.FromSeconds(config.Load().Downloads.StableSeconds)) continue;
                    if (File.Exists(path + ".part") || File.Exists(path + ".crdownload")) continue;
                    try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    _pending.Remove(path); _observed[path] = stamp;
                    var name = Path.GetFileName(path);
                    var settings = config.Load();
                    await send(new Notification
                    {
                        Id = "file:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path.ToUpperInvariant()}:{stamp.CreatedTicks}:{stamp.ModifiedTicks}:{stamp.Length}"))),
                        Title = MessageTemplate.Expand(onlyFile is null ? settings.Notifications.DownloadCompleted : settings.Notifications.FileReady, ("filename", name)),
                        Source = onlyFile is null ? "downloads" : "file",
                        Level = "success",
                        Message = $"文件：{name}\n大小：{FormatSize(stamp.Length)}\n目录：{Path.GetDirectoryName(path)}"
                    }, ct);
                    if (onlyFile is not null) return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Dispose(); Status = "已停止"; }
    }
    public static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:F2} MB" : bytes >= 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes} B";
    public static string DownloadsPath()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        if (result != 0) throw new AppException("DOWNLOADS_PATH", "无法定位 Windows 下载目录，请手动添加目录。");
        try { return Marshal.PtrToStringUni(pointer)!; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    public void Dispose() { foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
}
