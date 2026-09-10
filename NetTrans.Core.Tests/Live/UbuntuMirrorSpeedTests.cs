using NetTrans.Download;
using NetTrans.Net;
using NetTrans.Verify;
using Xunit;
using Xunit.Abstractions;

namespace NetTrans.Tests.Live;

/// <summary>
/// 各地区节点测速: how fast Ubuntu's mirrors actually are from wherever this is
/// running, measured through the transport the downloader itself uses.
///
/// Run it with:
///     NETTRANS_LIVE=1 dotnet test --filter FullyQualifiedName~NetTrans.Tests.Live -l "console;verbosity=detailed"
///
/// The result is a table, and it is about the machine that ran it: from a CI
/// runner in Azure, a mirror in Beijing measures as slow because it is far
/// away, not because it is a bad mirror. Run it where you download.
/// </summary>
public class UbuntuMirrorSpeedTests
{
    /// <summary>Enough to leave TCP slow start behind; small enough that twelve mirrors take minutes, not an hour.</summary>
    private const long Budget = 8 * 1024 * 1024;

    /// <summary>No mirror gets to own the run. A slow one still reports whatever it managed in this long.</summary>
    private static readonly TimeSpan PerMirror = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;

    public UbuntuMirrorSpeedTests(ITestOutputHelper output) => _output = output;

    [LiveFact]
    public async Task Measures_every_regional_mirror()
    {
        using var transport = new HttpTransport(userAgent: "netX/1.0 (mirror speed test)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var samples = new List<SpeedSample>();

        // One at a time, and reported as they land: twelve mirrors sharing one
        // link would each measure a twelfth of it.
        foreach (var (name, url) in UbuntuMirrors.All())
        {
            var sample = await SpeedProbe.MeasureAsync(
                transport, name, url, Budget, PerMirror, SystemClock.Instance, cancellation.Token);

            samples.Add(sample);
            _output.WriteLine($"{name,-22} {(sample.Ok ? $"{sample.BytesPerSecond / 1024 / 1024:F2} MB/s" : sample.Error)}");
        }

        string report = string.Join('\n', new[]
        {
            $"## Ubuntu 各地区节点测速（{UbuntuMirrors.Path}，每个节点 8 MB / 15 s 上限）",
            "",
            SpeedReport.Table(samples),
            "",
            SpeedReport.Verdict(samples),
            "",
            "> 数字属于跑这个测试的机器。在 CI runner 上跑，结果反映的是 runner 到各节点的链路。",
        });

        _output.WriteLine("");
        _output.WriteLine(report);

        if (Environment.GetEnvironmentVariable("NETTRANS_SPEED_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, report + "\n");
        }

        // The only pass mark: our transport can range-read a real mirror. Which
        // mirror won is the point of the run, not a condition of it.
        Assert.Contains(samples, sample => sample.Ok && sample.Bytes == Budget);
    }

    /// <summary>
    /// The other half of a download: Ubuntu publishes SHA256SUMS beside every
    /// release, which is exactly what 联网核对 goes looking for. This proves the
    /// lookup works against a real publisher rather than against our own fake.
    /// </summary>
    [LiveFact]
    public async Task Finds_the_published_checksum_for_a_real_release()
    {
        using var transport = new HttpTransport(userAgent: "NetTrans/1.0 (checksum lookup test)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var directory = new Uri("https://releases.ubuntu.com/24.04/");

        // Read the list first and take whatever ISO is current, so a point
        // release does not turn this into a 404 six months from now.
        var published = ChecksumFile.Parse(
            await PageReader.ReadAsync(transport, new Uri(directory, "SHA256SUMS"), 512 * 1024, cancellation.Token));

        var iso = published.FirstOrDefault(entry => entry.Name?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(iso);

        _output.WriteLine($"{iso!.Name} → {iso.Hash}");

        var found = await OnlineChecksums.LookupAsync(
            transport, new Uri(directory, iso.Name!), iso.Name!, cancellation.Token);

        Assert.NotNull(found);
        Assert.Equal(iso.Hash, found!.Hash);
        Assert.Equal(HashKind.Sha256, found.Kind);

        _output.WriteLine($"命中 {found.Source}");
    }
}
