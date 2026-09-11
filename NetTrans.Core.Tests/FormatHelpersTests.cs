using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// FormatHelpers against mb() / spd() / eta() as the handoff actually evaluates
/// them. Every expectation here was produced by running the prototype's own
/// functions; see tools/golden/.
/// </summary>
public class FormatHelpersTests
{
    public static TheoryData<long, string> ByteCases() => Build(Golden.Data.Bytes.Select(b => (b.Bytes, b.Expected)));

    public static TheoryData<long, string> SpeedCases() => Build(Golden.Data.Speeds.Select(s => (s.BytesPerSecond, s.Expected)));

    public static TheoryData<double, string> EtaCases()
    {
        var data = new TheoryData<double, string>();
        foreach (var eta in Golden.Data.Etas) data.Add(eta.Seconds, eta.Expected);
        return data;
    }

    public static TheoryData<long, long, string> EtaFromTaskCases()
    {
        var data = new TheoryData<long, long, string>();
        foreach (var eta in Golden.Data.EtaFromTask) data.Add(eta.RemainingBytes, eta.BytesPerSecond, eta.Expected);
        return data;
    }

    public static TheoryData<long, string> MidpointByteCases() => Build(Golden.Data.Midpoints.Bytes.Select(b => (b.Bytes, b.Expected)));

    public static TheoryData<long, string> MidpointSpeedCases() => Build(Golden.Data.Midpoints.Speeds.Select(s => (s.BytesPerSecond, s.Expected)));

    [Theory]
    [MemberData(nameof(ByteCases))]
    public void Bytes_matches_the_prototype(long bytes, string expected) =>
        Assert.Equal(expected, FormatHelpers.Bytes(bytes));

    [Theory]
    [MemberData(nameof(SpeedCases))]
    public void Speed_matches_the_prototype(long bytesPerSecond, string expected) =>
        Assert.Equal(expected, FormatHelpers.Speed(bytesPerSecond));

    /// <summary>
    /// 种子内容那一屏，设计稿摆的四个大小。它们在原型里是写死的字符串
    /// 而不是 mb() 算出来的，所以这里断言的是那四个样本本身。
    ///
    /// 顺带钉住 mb() 在这一档上的差别：1 KB 的文件用 Bytes() 会显示成
    /// "0 MB"，那正是 v0.1.13 截图里的样子。
    /// </summary>
    [Theory]
    [InlineData(3_371_549_327L, "3.14 GB")]
    [InlineData(190_840_832L, "182 MB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(2048L, "2 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(0L, "0 KB")]
    public void FileSize_spans_KB_to_GB(long bytes, string expected) =>
        Assert.Equal(expected, FormatHelpers.FileSize(bytes));

    [Fact]
    public void FileSize_differs_from_mb_only_below_a_megabyte()
    {
        Assert.Equal("0 MB", FormatHelpers.Bytes(1024));
        Assert.Equal("1 KB", FormatHelpers.FileSize(1024));

        foreach (long bytes in new[] { 1024L * 1024, 190_840_832L, 3_371_549_327L })
        {
            Assert.Equal(FormatHelpers.Bytes(bytes), FormatHelpers.FileSize(bytes));
        }
    }

    [Theory]
    [MemberData(nameof(EtaCases))]
    public void Eta_matches_the_prototype(double seconds, string expected) =>
        Assert.Equal(expected, FormatHelpers.EtaFromSeconds(seconds));

    [Theory]
    [MemberData(nameof(EtaFromTaskCases))]
    public void Eta_from_remaining_bytes_matches_the_prototype(long remaining, long speed, string expected) =>
        Assert.Equal(expected, FormatHelpers.Eta(remaining, speed));

    /// <summary>
    /// JavaScript rounds halves away from zero; .NET rounds them to even. These
    /// are the cases where the two disagree, so they pin the behaviour down.
    /// </summary>
    [Theory]
    [MemberData(nameof(MidpointByteCases))]
    public void Bytes_rounds_halves_away_from_zero(long bytes, string expected) =>
        Assert.Equal(expected, FormatHelpers.Bytes(bytes));

    [Theory]
    [MemberData(nameof(MidpointSpeedCases))]
    public void Speed_rounds_halves_away_from_zero(long bytesPerSecond, string expected) =>
        Assert.Equal(expected, FormatHelpers.Speed(bytesPerSecond));

    [Fact]
    public void Speed_is_empty_when_stalled() => Assert.Equal("", FormatHelpers.Speed(0));

    [Fact]
    public void SpeedOrDash_shows_an_em_dash_when_stalled() => Assert.Equal("—", FormatHelpers.SpeedOrDash(0));

    [Fact]
    public void Eta_is_unknown_when_stalled() => Assert.Equal("计算中", FormatHelpers.Eta(1024, 0));

    [Theory]
    [MemberData(nameof(SpeedCases))]
    public void SpeedParts_recombine_into_Speed(long bytesPerSecond, string expected)
    {
        var (value, unit) = FormatHelpers.SpeedParts(bytesPerSecond);
        if (expected.Length == 0) return; // the split form has no empty case; it always shows a unit
        Assert.Equal(expected, $"{value} {unit}");
    }

    /// <summary>Culture must not leak into the decimal separator.</summary>
    [Fact]
    public void Formatting_is_culture_invariant()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("5.80 GB", FormatHelpers.Bytes(5940L * 1024 * 1024));
            Assert.Equal("1.2 MB/s", FormatHelpers.Speed(1180L * 1024));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    private static TheoryData<long, string> Build(IEnumerable<(long Value, string Expected)> cases)
    {
        var data = new TheoryData<long, string>();
        foreach (var (value, expected) in cases) data.Add(value, expected);
        return data;
    }
}
