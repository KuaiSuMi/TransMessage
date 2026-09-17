using System.Security.Cryptography;
using System.Text;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Security;

public sealed class SecretStore(AppPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TransMessage.v1.windows.user");
    private string SecretPath(string key) => Path.Combine(paths.Root, key switch
    {
        "qq" => "qq.dpapi",
        "api" => "api.dpapi",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    });
    public string? Get(string key)
    {
        var path = SecretPath(key);
        if (!File.Exists(path)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Set(string key, string value)
    {
        var clear = Encoding.UTF8.GetBytes(value);
        try { AtomicFile.WriteBytes(SecretPath(key), ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public string ApiToken() => AtomicFile.Locked(SecretPath("api"), () =>
    {
        var token = Get("api");
        if (token is not null) return token;
        return RegenerateApiToken();
    });
    public string RegenerateApiToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Set("api", token); return token;
    }
}
