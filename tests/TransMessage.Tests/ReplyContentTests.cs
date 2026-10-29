using System.Diagnostics;
using System.Text;
using TransMessage.Core.Codex;

namespace TransMessage.Tests;

public sealed class ReplyContentTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(2600)]
    public void SplitPreservesUnicodeAndEveryByteAcrossBoundaries(int limit)
    {
        var text = string.Concat(Enumerable.Repeat("中文😀👨‍👩‍👧‍👦 cafe\u0301\r\n[附件](<D:/文档/报告 (1).pdf>)\n", 150));
        var parts = ReplyContent.Split(text, limit);
        var utf8 = new UTF8Encoding(false, true);
        Assert.True(parts.Count > 1);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part => Assert.InRange(utf8.GetByteCount(part), 1, limit));
        Assert.Equal(utf8.GetBytes(text), parts.SelectMany(utf8.GetBytes).ToArray());
    }

    [Fact]
    public void SplitHandlesEmptyTextAndExactFourByteBoundary()
    {
        Assert.Empty(ReplyContent.Split(""));
        Assert.Equal(new[] { "😀", "😀", "abc", "文" }, ReplyContent.Split("😀😀abc文", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplyContent.Split("text", 3));
    }

    [Fact]
    public void FilesResolveUnicodeSpacesAndParenthesesInAbsoluteAndRelativeLinks()
    {
        using var data = new TestData();
        var document = CreateFile(data, "结果 (终稿)", "中文 报告 (1).pdf");
        var image = CreateFile(data, "结果 (终稿)", "示意图 (2).png");
        var markdown = $"[报告](<{document}>)\n![图片](<结果 (终稿)/示意图 (2).png>)";
        Assert.Equal(new[] { document, image }, ReplyContent.Files(markdown, data.Paths.Root));
    }

    [Fact]
    public void FilesResolveLeadingSlashDrivePathsAndReferenceStyleLinks()
    {
        using var data = new TestData();
        var file = CreateFile(data, "文档", "a (b).txt");
        var slash = file.Replace('\\', '/');
        var markdown = $"[下载][report]\n\n[report]: </{slash}> \"报告\"";
        Assert.Equal(file, Assert.Single(ReplyContent.Files(markdown, data.Paths.Root)));
    }

    [Fact]
    public void FilesResolveRelativeParentLinksAgainstTheThreadDirectory()
    {
        using var data = new TestData();
        var file = CreateFile(data, "输出", "报告.txt");
        var directory = Path.Combine(data.Paths.Root, "项目"); Directory.CreateDirectory(directory);
        Assert.Equal(file, Assert.Single(ReplyContent.Files("[报告](<../输出/报告.txt>)", directory)));
    }

    [Theory]
    [InlineData(":12")]
    [InlineData(":12:3")]
    [InlineData("#L12")]
    [InlineData("#L12-L19")]
    [InlineData("#12")]
    [InlineData("#12-19")]
    public void FilesStripLineAndColumnAnnotations(string annotation)
    {
        using var data = new TestData();
        var file = CreateFile(data, "源码", "程序 (甲).cs");
        Assert.Equal(file, Assert.Single(ReplyContent.Files($"[代码](<{file.Replace('\\', '/')}{annotation}>)", data.Paths.Root)));
    }

    [Fact]
    public void FileUrisDecodeUnicodeAndLiteralPercentExactlyOnce()
    {
        using var data = new TestData();
        var expected = CreateFile(data, "报告", "完成%20进度 (1).txt");
        _ = CreateFile(data, "报告", "完成 进度 (1).txt");
        var uri = FileUri(expected);
        Assert.Contains("%25", uri, StringComparison.Ordinal);
        Assert.Equal(expected, Assert.Single(ReplyContent.Files($"[报告](<{uri}>)", data.Paths.Root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodedHashInTheFileNameIsNotTreatedAsALineAnchor(bool fileUri)
    {
        using var data = new TestData();
        var expected = CreateFile(data, "输出", "附件#12");
        _ = CreateFile(data, "输出", "附件");
        var url = fileUri ? FileUri(expected) : expected.Replace('\\', '/').Replace("#", "%23", StringComparison.Ordinal);
        Assert.Equal(expected, Assert.Single(ReplyContent.Files($"[附件](<{url}>)", data.Paths.Root)));
    }

    [Fact]
    public void EncodedHashNamesCanStillHaveASeparateLineAnchor()
    {
        using var data = new TestData();
        var expected = CreateFile(data, "输出", "附件#12");
        var url = expected.Replace('\\', '/').Replace("#", "%23", StringComparison.Ordinal);
        Assert.Equal(expected, Assert.Single(ReplyContent.Files($"[附件](<{url}#L8>)", data.Paths.Root)));
    }

    [Fact]
    public void FilesIgnoreInlineCodeFencedCodeAndPlainHtml()
    {
        using var data = new TestData();
        var file = CreateFile(data, "example.txt"); var link = $"[示例](<{file.Replace('\\', '/')}>)";
        var markdown = $"`{link}`\n\n```markdown\n{link}\n```\n\n<a href=\"{file.Replace('\\', '/')}\">附件</a>";
        Assert.Empty(ReplyContent.Files(markdown, data.Paths.Root));
    }

    [Fact]
    public void FilesDeduplicateResolvedPathsAndPreserveFirstAppearance()
    {
        using var data = new TestData();
        var first = CreateFile(data, "结果", "Report.pdf"); var second = CreateFile(data, "结果", "Image.png");
        var markdown = $"[报告](<{first}>)\n![图](<结果/Image.png>)\n[重复](<结果/../结果/report.PDF>)\n[再重复](<{FileUri(first)}>)";
        Assert.Equal(new[] { first, second }, ReplyContent.Files(markdown, data.Paths.Root));
    }

    [Fact]
    public void FilesRejectNetworkDeviceAndNamedAlternateStreamLinks()
    {
        using var data = new TestData();
        var file = CreateFile(data, "report.txt");
        var links = new[]
        {
            "https://example.com/report.txt", "smb://server/share/report.txt", "file://server/share/report.txt",
            @"\\server\share\report.txt", "//server/share/report.txt", @"\\?\" + file, @"\\.\" + file,
            "//?/" + file.Replace('\\', '/'), file + ":secret", file + "::$DATA", "sandbox:/mnt/data/report.txt"
        };
        foreach (var link in links)
            Assert.Empty(ReplyContent.Files($"[附件](<{link.Replace("\\", "\\\\", StringComparison.Ordinal)}>)", data.Paths.Root));
    }

    [Fact]
    public void FilesIgnoreMissingPathsAndDirectories()
    {
        using var data = new TestData();
        Assert.Empty(ReplyContent.Files($"[缺失](<missing.txt>)\n[目录](<{data.Paths.Root}>)", data.Paths.Root));
    }

    [Fact]
    public void FilesRejectFilesInsideJunctionDirectories()
    {
        using var data = new TestData();
        _ = CreateFile(data, "actual", "target.txt"); var link = Path.Combine(data.Paths.Root, "alias");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["TM_TEST_JUNCTION"] = link; start.Environment["TM_TEST_TARGET"] = Path.Combine(data.Paths.Root, "actual");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:TM_TEST_JUNCTION -Target $env:TM_TEST_TARGET | Out-Null");
        using var process = Process.Start(start)!; var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Empty(ReplyContent.Files("[附件](<alias/target.txt>)", data.Paths.Root));
        }
        finally { Directory.Delete(link); }
    }

    private static string FileUri(string path)
    {
        var slash = path.Replace('\\', '/');
        return "file:///" + slash[..3] + string.Join("/", slash[3..].Split('/').Select(Uri.EscapeDataString));
    }

    private static string CreateFile(TestData data, params string[] segments)
    {
        var path = Path.Combine([data.Paths.Root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path;
    }
}
