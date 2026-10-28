using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Channels;

public sealed class QqImageCache : IDisposable
{
    public const int MaximumImageCount = 8;
    public const int MaximumImageBytes = 10 * 1024 * 1024;
    private const long MaximumCacheBytes = 512L * 1024 * 1024;
    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _downloads = new(1, 1);

    public QqImageCache(AppPaths paths, HttpClient? http = null)
    {
        _directory = Path.Combine(paths.Root, "qq-images");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x))) throw UnsafeAddress();
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("图片服务器连接失败。");
            }
        });
    }

    public async Task<IReadOnlyList<string>> DownloadAsync(IReadOnlyList<QqImageAttachment> images, CancellationToken ct = default)
    {
        if (images.Count > MaximumImageCount) throw new AppException("QQ_IMAGE_COUNT", $"一次最多发送 {MaximumImageCount} 张图片。");
        if (images.Count == 0) return [];
        await _downloads.WaitAsync(ct);
        List<string> created = [];
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (var file in CacheFiles().Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))) file.Delete();
            var cachedBytes = CacheFiles().Sum(x => x.Length);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(90));
            List<string> paths = [];
            foreach (var image in images)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (image.Size is < 0 or > MaximumImageBytes) throw TooLarge();
                if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw InvalidImage();
                var uri = ValidateUrl(image.Url);
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                if ((int)response.StatusCode is >= 300 and < 400) throw UnsafeAddress();
                if (!response.IsSuccessStatusCode) throw new AppException("QQ_IMAGE_DOWNLOAD", "图片下载失败，请重新发送图片。");
                if (response.Content.Headers.ContentLength > MaximumImageBytes) throw TooLarge();
                var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
                created.Add(temporary);
                long size = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(budget.Token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; int count;
                    while ((count = await input.ReadAsync(buffer, budget.Token)) > 0)
                    {
                        size += count;
                        if (size > MaximumImageBytes) throw TooLarge();
                        if (cachedBytes + size > MaximumCacheBytes) throw new AppException("QQ_IMAGE_CACHE_FULL", "图片缓存已满，请清理旧图片后重试。");
                        await output.WriteAsync(buffer.AsMemory(0, count), budget.Token);
                    }
                }
                var extension = ImageExtension(temporary, budget.Token);
                var path = Path.ChangeExtension(temporary, extension);
                File.Move(temporary, path); created.Add(path); paths.Add(path); cachedBytes += size;
            }
            budget.Token.ThrowIfCancellationRequested();
            return paths;
        }
        catch (HttpRequestException) { Cleanup(created); throw new AppException("QQ_IMAGE_DOWNLOAD", "图片下载失败，请检查网络或重新发送图片。"); }
        catch { Cleanup(created); throw; }
        finally { _downloads.Release(); }
    }

    private IEnumerable<FileInfo> CacheFiles() => new DirectoryInfo(_directory).EnumerateFiles()
        .Where(x => Guid.TryParseExact(Path.GetFileNameWithoutExtension(x.Name), "N", out _));

    private static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublic(address)) throw UnsafeAddress();
        return uri;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] is 0 or 168)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 198 && b[1] is 18 or 19)
                && !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20
            && address.ScopeId == 0 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
    }

    private static string ImageExtension(string path, CancellationToken ct)
    {
        try
        {
            using var file = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var extension = decoder switch { PngBitmapDecoder => ".png", JpegBitmapDecoder => ".jpg", GifBitmapDecoder => ".gif", _ => throw InvalidImage() };
            if (decoder.Frames.Count is 0 or > 256) throw InvalidImage();
            long pixels = 0;
            foreach (var frame in decoder.Frames)
            {
                ct.ThrowIfCancellationRequested();
                var width = frame.PixelWidth; var height = frame.PixelHeight;
                pixels += (long)width * height;
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || pixels > 40_000_000)
                    throw new AppException("QQ_IMAGE_DIMENSIONS", "图片尺寸或动画总像素超过限制，请缩小后重新发送。");
                var bits = frame.Format.BitsPerPixel;
                if (bits is <= 0 or > 64) throw InvalidImage();
                var stride = checked((width * bits + 7) / 8);
                var buffer = new byte[checked(stride * Math.Min(height, 64))];
                // Decode every pixel in bounded strips, so a plausible header alone is insufficient.
                for (var row = 0; row < height; row += 64)
                {
                    ct.ThrowIfCancellationRequested();
                    frame.CopyPixels(new Int32Rect(0, row, width, Math.Min(64, height - row)), buffer, stride, 0);
                }
            }
            return extension;
        }
        // WIC also reports truncated image streams as IOException wrapping a COM error.
        catch (Exception e) when (e is FileFormatException or NotSupportedException or ArgumentException or COMException or OverflowException
            or IOException { InnerException: COMException })
        { ct.ThrowIfCancellationRequested(); throw InvalidImage(); }
    }
    private static AppException InvalidImage() => new("QQ_IMAGE_FORMAT", "图片格式无效，仅支持 PNG、JPEG 和 GIF。");
    private static AppException TooLarge() => new("QQ_IMAGE_SIZE", "单张图片不得超过 10 MB，请压缩后重新发送。");
    private static AppException UnsafeAddress() => new("QQ_IMAGE_URL", "图片地址必须为安全的公网 HTTPS 地址，且不能重定向。");
    private static void Cleanup(IEnumerable<string> paths)
    {
        foreach (var path in paths) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
