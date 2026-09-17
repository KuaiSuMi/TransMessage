using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomlyn;

namespace TransMessage.Core.Configuration;

public sealed class AppPaths
{
    public string Root { get; }
    public string Config => Path.Combine(Root, "config.toml");
    public string Logs => Path.Combine(Root, "logs");
    public string InstanceId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
    public AppPaths(string? root = null, bool portable = false)
    {
        Root = Path.GetFullPath(root ?? (portable || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransMessage")));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } value
        ? Path.GetFullPath(value) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
}

public static class AtomicFile
{
    public static void Write(string path, string content) => WriteBytes(path, Encoding.UTF8.GetBytes(content));
    public static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Locked<T>(string key, Func<T> action)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, @"Local\TransMessage-" + digest);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new AppException("STORE_BUSY", "存储正忙，请稍后重试。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}

public sealed class ConfigStore(AppPaths paths)
{
    private static readonly TomlSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public AppPaths Paths { get; } = paths;
    public AppConfig Load()
    {
        if (!File.Exists(Paths.Config)) return new AppConfig();
        AppConfig config;
        try { config = TomlSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.Config), Options) ?? throw new FormatException(); }
        catch (Exception e) when (e is not IOException and not UnauthorizedAccessException)
        { throw new AppException("CONFIG_PARSE", "配置解析失败，原文件未修改。请检查 config.toml。"); }
        config.Validate();
        return config;
    }
    public void Save(AppConfig config) => AtomicFile.Locked(Paths.Config, () => { SaveCore(config); return true; });
    public AppConfig Update(Action<AppConfig> edit) => AtomicFile.Locked(Paths.Config, () =>
    {
        var config = Load(); edit(config); SaveCore(config); return config;
    });
    private void SaveCore(AppConfig config)
    {
        config.Validate();
        AtomicFile.Write(Paths.Config, TomlSerializer.Serialize(config, Options));
    }
}
