using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Channels;

public sealed partial class QqClient
{
    public const long MaximumFileBytes = 200L * 1024 * 1024;
    public const int DirectFileBytes = 5 * 1024 * 1024;
    private const int PrefixHashBytes = 10_002_432;
    private static readonly HttpClient PublicUploads = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(x => !IsPublicUploadAddress(x))) throw UploadAddressError();
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("文件上传服务器连接失败。");
        }
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _uploadHttp = uploadHttp ?? PublicUploads;

    public async Task<QqSendReceipt> SendFileAsync(QqTarget target, string path, CancellationToken ct, string? replyId = null)
    {
        if (target.Kind != "c2c") throw new AppException("QQ_FILE_TARGET", "文件发送仅支持 QQ 私聊。");
        if (replyId is not null) ReplySequence(target, replyId, false);
        ct.ThrowIfCancellationRequested();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new AppException("QQ_FILE_SIZE", "文件必须非空且不超过 200 MB。");
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new AppException("QQ_FILE_NAME", "文件名无效。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromMinutes(20));
        var scope = $"v2/users/{Uri.EscapeDataString(target.Id)}";
        string fileInfo;
        if (file.Length <= DirectFileBytes)
        {
            var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, budget.Token);
            using var uploaded = await RequestAsync(HttpMethod.Post, scope + "/files",
                new { file_type = 4, file_name = name, file_data = Convert.ToBase64String(bytes), srv_send_msg = false }, budget.Token, 90);
            fileInfo = UploadText(uploaded.RootElement, "file_info");
        }
        else fileInfo = await UploadChunksAsync(scope, name, file, budget.Token);
        budget.Token.ThrowIfCancellationRequested();
        var body = new Dictionary<string, object> { ["msg_type"] = 7, ["media"] = new { file_info = fileInfo } };
        AddReply(body, target, replyId);
        using var sent = await RequestAsync(HttpMethod.Post, scope + "/messages", body, budget.Token);
        return ReadReceipt(sent.RootElement);
    }

    private async Task<string> UploadChunksAsync(string scope, string name, FileStream file, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[81920]; long hashed = 0; int count;
        while ((count = await file.ReadAsync(buffer, ct)) > 0)
        {
            md5.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
            if (hashed < PrefixHashBytes) prefix.AppendData(buffer, 0, (int)Math.Min(count, PrefixHashBytes - hashed));
            hashed += count;
        }
        if (hashed != file.Length) throw UploadSchemaError();
        using var prepared = await RequestAsync(HttpMethod.Post, scope + "/upload_prepare", new
        {
            file_type = 4,
            file_name = name,
            file_size = file.Length.ToString(CultureInfo.InvariantCulture),
            md5 = Convert.ToHexStringLower(md5.GetHashAndReset()),
            sha1 = Convert.ToHexStringLower(sha1.GetHashAndReset()),
            md5_10m = Convert.ToHexStringLower(prefix.GetHashAndReset())
        }, ct, 90);
        var root = prepared.RootElement; var uploadId = UploadText(root, "upload_id");
        var blockSize = UploadSize(root, "block_size");
        if (blockSize <= 0 || blockSize > MaximumFileBytes || !root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array
            || parts.GetArrayLength() is 0 or > 1024 || parts.GetArrayLength() != (file.Length + blockSize - 1) / blockSize) throw UploadSchemaError();
        var retrySeconds = 300; var retryDelay = 1;
        if (root.TryGetProperty("upload_config", out var config))
        {
            if (config.ValueKind != JsonValueKind.Object) throw UploadSchemaError();
            if (config.TryGetProperty("retry_timeout", out var timeout))
            {
                if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var seconds) || seconds <= 0) throw UploadSchemaError();
                retrySeconds = Math.Min(seconds, 600);
            }
            if (config.TryGetProperty("retry_delay", out var delay))
            {
                if (delay.ValueKind != JsonValueKind.Number || !delay.TryGetInt32(out retryDelay) || retryDelay < 0 || retryDelay > 60) throw UploadSchemaError();
            }
        }
        var ordered = parts.EnumerateArray().Select(part =>
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("index", out var index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number)) throw UploadSchemaError();
            return (Index: number, Size: UploadSize(part, "block_size"), Url: UploadAddress(UploadText(part, "presigned_url")));
        }).OrderBy(part => part.Index).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var part = ordered[i]; var expected = Math.Min(blockSize, file.Length - i * blockSize);
            if (part.Index != i || part.Size != expected) throw UploadSchemaError();
        }
        file.Position = 0;
        foreach (var part in ordered)
        {
            var bytes = new byte[checked((int)part.Size)]; await file.ReadExactlyAsync(bytes, ct);
            using var retryBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            retryBudget.CancelAfter(TimeSpan.FromSeconds(retrySeconds));
            await PutPartAsync(part.Url, bytes, retryDelay, retryBudget.Token);
            using var finished = await RequestAsync(HttpMethod.Post, scope + "/upload_part_finish", new
            {
                upload_id = uploadId,
                part_index = part.Index,
                block_size = part.Size.ToString(CultureInfo.InvariantCulture),
                md5 = Convert.ToHexStringLower(MD5.HashData(bytes))
            }, retryBudget.Token, 90);
        }
        using var completed = await RequestAsync(HttpMethod.Post, scope + "/files",
            new { file_type = 4, file_name = name, upload_id = uploadId, srv_send_msg = false }, ct, 90);
        return UploadText(completed.RootElement, "file_info");
    }

    private async Task PutPartAsync(Uri url, byte[] bytes, int retryDelay, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
                using var response = await _uploadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return;
                if ((int)response.StatusCode is >= 300 and < 400) throw UploadAddressError();
                if (attempt >= 2 || (int)response.StatusCode is not (408 or 429 or >= 500))
                    throw new AppException("QQ_FILE_UPLOAD", "文件分片上传失败，请重试。");
            }
            catch (HttpRequestException)
            {
                if (attempt >= 2) throw new AppException("QQ_FILE_UPLOAD", "文件分片上传中断，请检查网络后重试。");
            }
            await Task.Delay(TimeSpan.FromSeconds(retryDelay), ct);
        }
    }

    private static string UploadText(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 65536 } text
        ? text : throw UploadSchemaError();
    private static long UploadSize(JsonElement data, string name) => long.TryParse(UploadText(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
        ? size : throw UploadSchemaError();

    private static Uri UploadAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublicUploadAddress(address)) throw UploadAddressError();
        return uri;
    }

    private static bool IsPublicUploadAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] is 0 or 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 198 && b[1] is 18 or 19) && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 && address.ScopeId == 0
            && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
    }

    private static AppException UploadSchemaError() => new("QQ_FILE_SCHEMA", "QQ 文件上传响应无效，请稍后重试。");
    private static AppException UploadAddressError() => new("QQ_FILE_URL", "QQ 文件上传地址无效，请稍后重试。");
}
