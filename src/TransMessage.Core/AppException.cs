namespace TransMessage.Core;

public sealed class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public static string Describe(Exception error) => error switch
    {
        AppException app => $"{app.Message} [{app.Code}]",
        OperationCanceledException => "操作已取消或超时。 [CANCELLED]",
        System.Security.Cryptography.CryptographicException => "无法解密凭证。请在当前 Windows 账户重新输入。 [DPAPI]",
        UnauthorizedAccessException => "无法访问文件或目录，请检查当前账户权限。 [ACCESS_DENIED]",
        IOException => "文件操作失败，请检查路径、占用状态和可用空间。 [IO]",
        System.Net.Http.HttpRequestException => "网络连接失败，请检查网络和代理。 [NETWORK]",
        _ => $"操作失败，请运行诊断。 [{error.GetType().Name}]"
    };
}
