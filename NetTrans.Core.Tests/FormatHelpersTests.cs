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
