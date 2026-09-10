using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Verify;
using Xunit;
using Xunit.Abstractions;

namespace NetTrans.Tests.Live;

/// <summary>
/// 实际下载 ubuntu: the whole path, against the real releases.ubuntu.com --
/// probe, split into ranges, write to a real disk, hash what landed.
///
/// Everything else in the suite runs against <c>FakeHttpTransport</c>, which
/// answers every request perfectly. That proves the queueing rules and the
/// segment arithmetic, and proves nothing at all about whether a download from
/// the actual internet works. This is the test that does.
///
/// Run it with:
///     NETTRANS_LIVE=1 dotnet test --filter FullyQualifiedName~UbuntuDownloadTests -l "console;verbosity=detailed"
/// </summary>
public class UbuntuDownloadTests : IDisposable
{
    /// <summary>The 24.04 LTS directory. Point releases come and go inside it; the directory itself does not.</summary>
    private static readonly Uri Directory24_04 = new("https://releases.ubuntu.com/24.04/");

    /// <summary>A point release Canonical has already removed. Kept deliberately: it is the 404 case.</summary>
    private const string RemovedRelease = "https://releases.ubuntu.com/24.04/ubuntu-24.04.2-desktop-amd64.iso";

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-live-" + Guid.NewGuid().ToString("N"));

    public UbuntuDownloadTests(ITestOutputHelper output)
    {
        _output = output;
        System.IO.Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    /// <summary>
    /// A whole file, start to finish, through the engine the app runs.
    ///
    /// The file is the release manifest rather than the ISO: 6 GB through CI
    /// proves nothing the manifest does not, and the segment size is turned
    /// down to 16 KB so a small file still gets split across four connections.
    /// Splitting and reassembling is the part that a fake server cannot test
    /// honestly, because a fake server always honours a range request.
    /// </summary>
    [LiveFact]
    public async Task Downloads_a_real_file_end_to_end()
    {
        using var transport = new HttpTransport(userAgent: "NetTrans/1.0 (live download test)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var manifest = await CurrentManifestAsync(transport, cancellation.Token);
        _output.WriteLine($"源地址 {manifest}");

        var probe = await transport.ProbeAsync(manifest, cancellation.Token);
        _output.WriteLine($"探测 长度={probe.Length} 支持断点={probe.SupportsRanges} 可分段={probe.CanSplit}");
        Assert.True(probe.CanSplit, "releases.ubuntu.com 应当支持 Range 请求");

        await using var engine = new DownloadEngine(
            transport,
            FileSinkFactory.Instance,
            SystemClock.Instance,
            options: new DownloadOptions(
                Connections: 4,
                MinimumSegmentLength: 16 * 1024,
                MaxRetries: 2,
                RetryDelay: TimeSpan.FromSeconds(1)),
            maxConcurrent: 1);

        var item = new DownloadItem
        {
            Id = 1,
            Name = probe.FileName,
            Host = manifest.Host,
            Kind = FileKind.Doc,
            Size = 0, // the probe fills it in, exactly as it does for a pasted URL
            Category = "doc",
            Url = manifest.AbsoluteUri,
            SavePath = _directory,
            RequestedConnections = 4,
        };

        engine.Add(item);
        await Until(() => item.Status is DownloadStatus.Completed or DownloadStatus.Error, TimeSpan.FromMinutes(2));

        Assert.True(
            item.Status == DownloadStatus.Completed,
            $"下载没有完成：{item.Status} {item.ErrorMessage}");

        string path = Path.Combine(_directory, item.Name);
        var landed = await File.ReadAllBytesAsync(path, cancellation.Token);

        Assert.Equal(probe.Length, landed.LongLength);
        Assert.Equal(probe.Length, item.Done);

        // The bytes have to match what a single unsegmented read returns --
        // four connections writing into one file at four offsets is exactly
        // where an off-by-one in the segment plan would hide.
        var whole = await ReadWholeAsync(transport, manifest, cancellation.Token);
        Assert.Equal(Convert.ToHexString(whole), Convert.ToHexString(landed));

        _output.WriteLine($"落盘 {landed.LongLength} 字节，与单流读取逐字节一致");
    }

    /// <summary>
    /// 断点续传 against a real server: half the file, then resume the rest.
    ///
    /// The resume path re-opens the URL with a Range header and trusts the 206
    /// it gets back. A server that quietly answers 200 instead would overwrite
    /// the first half with the start of the file again, so this checks the
    /// reassembled bytes rather than just the length.
    /// </summary>
    [LiveFact]
    public async Task Resumes_a_real_file_from_the_middle()
    {
        using var transport = new HttpTransport(userAgent: "NetTrans/1.0 (live resume test)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var manifest = await CurrentManifestAsync(transport, cancellation.Token);
        var whole = await ReadWholeAsync(transport, manifest, cancellation.Token);
        long half = whole.LongLength / 2;

        await using (var head = await transport.OpenAsync(manifest, 0, half - 1, cancellation.Token))
        {
            var first = new MemoryStream();
            await head.CopyToAsync(first, cancellation.Token);
            Assert.Equal(half, first.Length);
            Assert.Equal(
                Convert.ToHexString(whole[..(int)half]),
                Convert.ToHexString(first.ToArray()));
        }

        await using var tail = await transport.OpenAsync(manifest, half, null, cancellation.Token);
        var rest = new MemoryStream();
        await tail.CopyToAsync(rest, cancellation.Token);

        Assert.Equal(whole.LongLength - half, rest.Length);
        Assert.Equal(
            Convert.ToHexString(whole[(int)half..]),
            Convert.ToHexString(rest.ToArray()));

        _output.WriteLine($"前 {half} 字节 + 从第 {half} 字节续传，拼回来与整file一致");
    }

    /// <summary>
    /// The 404 itself.
    ///
    /// Canonical deletes a point release from releases.ubuntu.com as soon as
    /// the next one ships, so a link that worked last month is gone -- which is
    /// the single most common way an ubuntu download fails. What is asserted is
    /// not that the URL is dead (that is Canonical's business) but that when it
    /// is, the task lands in 错误 with the status code in the message, instead
    /// of hanging, retrying forever, or writing a 0-byte file and calling it
    /// done.
    /// </summary>
    [LiveFact]
    public async Task A_removed_release_fails_with_the_status_code_in_the_message()
    {
        using var transport = new HttpTransport(userAgent: "NetTrans/1.0 (live 404 test)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var dead = new Uri(RemovedRelease);

        // If Canonical ever puts this file back, the test has nothing to say --
        // report it and stop rather than fail on someone else's mirror policy.
        var status = await StatusOfAsync(transport, dead, cancellation.Token);
        if (status != System.Net.HttpStatusCode.NotFound)
        {
            _output.WriteLine($"{dead} 现在返回 {(int)status}，这个用例需要一个真死链才有意义，跳过断言");
            return;
        }

        await using var engine = new DownloadEngine(
            transport,
            FileSinkFactory.Instance,
            SystemClock.Instance,
            options: new DownloadOptions(MaxRetries: 0, RetryDelay: TimeSpan.FromMilliseconds(1)),
            maxConcurrent: 1);

        var item = new DownloadItem
        {
            Id = 1,
            Name = "ubuntu-24.04.2-desktop-amd64.iso",
            Host = dead.Host,
            Kind = FileKind.Disc,
            Size = 0,
            Category = "soft",
            Url = dead.AbsoluteUri,
            SavePath = _directory,
            RequestedConnections = 4,
        };

        engine.Add(item);
        await Until(() => item.Status == DownloadStatus.Error, TimeSpan.FromMinutes(1));

        Assert.Contains("404", item.ErrorMessage ?? "");
        Assert.False(File.Exists(Path.Combine(_directory, item.Name)), "失败的任务不应该留下文件");

        _output.WriteLine($"死链报错：{item.ErrorMessage}");
    }

    /// <summary>
    /// Every ISO Canonical currently publishes a checksum for is actually
    /// there.
    ///
    /// This is the check that would have caught the dead link above while it
    /// was still ours to fix: SHA256SUMS is the release's own index, so a name
    /// in it that 404s means the link a user would copy is broken.
    /// </summary>
    [LiveFact]
    public async Task Every_release_the_checksum_list_names_is_downloadable()
    {
        using var transport = new HttpTransport(userAgent: "NetTrans/1.0 (live link check)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var published = ChecksumFile.Parse(await PageReader.ReadAsync(
            transport, new Uri(Directory24_04, "SHA256SUMS"), 512 * 1024, cancellation.Token));

        var isos = published
            .Where(entry => entry.Name?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.NotEmpty(isos);

        var dead = new List<string>();

        foreach (var entry in isos)
        {
            var url = new Uri(Directory24_04, entry.Name!);
            var status = await StatusOfAsync(transport, url, cancellation.Token);

            _output.WriteLine($"{entry.Name,-40} {(int)status}");
            if (status != System.Net.HttpStatusCode.OK) dead.Add($"{entry.Name} → {(int)status}");
        }

        Assert.True(dead.Count == 0, "SHA256SUMS 里点名的镜像取不到：" + string.Join("；", dead));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The current release's .manifest, found by reading the directory rather
    /// than by hard-coding a version -- the point release in the name is
    /// exactly the thing that rots.
    /// </summary>
    private static async Task<Uri> CurrentManifestAsync(IHttpTransport transport, CancellationToken cancellationToken)
    {
        string listing = await PageReader.ReadAsync(transport, Directory24_04, 512 * 1024, cancellationToken);

        var manifest = LinkExtractor.Extract(listing, Directory24_04)
            .Where(link => link.Extension == "manifest")
            .OrderByDescending(link => link.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        Assert.True(manifest is not null, "releases.ubuntu.com/24.04/ 里没有 .manifest，目录格式变了");
        return manifest!.Url;
    }

    private static async Task<byte[]> ReadWholeAsync(IHttpTransport transport, Uri url, CancellationToken cancellationToken)
    {
        await using var stream = await transport.OpenAsync(url, 0, null, cancellationToken);
        using var body = new MemoryStream();
        await stream.CopyToAsync(body, cancellationToken);
        return body.ToArray();
    }

    /// <summary>What the server says about a URL, without pulling the body down.</summary>
    private static async Task<System.Net.HttpStatusCode> StatusOfAsync(
        IHttpTransport transport, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            // One byte is enough to learn the status, and costs nothing even
            // when the answer is a 6 GB file.
            await using var stream = await transport.OpenAsync(url, 0, 0, cancellationToken);
            return System.Net.HttpStatusCode.OK;
        }
        catch (HttpRequestException failure) when (failure.StatusCode is { } status)
        {
            return status;
        }
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(200);
        }

        Assert.True(condition(), "等超时了");
    }
}
