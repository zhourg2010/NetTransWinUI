using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The one easing curve the design uses, cubic-bezier(.32,.72,0,1). The
/// expectations come from an independent bisection solver in the golden
/// generator, so a mistake in the Newton-Raphson implementation here shows up
/// rather than being reproduced on both sides.
/// </summary>
public class EasingTests
{
    public static TheoryData<double, double> Curve()
    {
        var data = new TheoryData<double, double>();
        foreach (var point in Golden.Data.Easing) data.Add(point.T, point.Expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(Curve))]
    public void Matches_the_curve(double t, double expected) =>
        Assert.Equal(expected, Easing.Standard(t), precision: 4);

    [Fact]
    public void Is_pinned_at_both_ends()
    {
        Assert.Equal(0d, Easing.Standard(0));
        Assert.Equal(1d, Easing.Standard(1));
    }

    [Fact]
    public void Clamps_outside_the_unit_interval()
    {
        Assert.Equal(0d, Easing.Standard(-1));
        Assert.Equal(1d, Easing.Standard(2));
    }

    [Fact]
    public void Never_goes_backwards()
    {
        double previous = -1;

        for (int i = 0; i <= 100; i++)
        {
            double value = Easing.Standard(i / 100.0);
            Assert.True(value >= previous, $"eased({i / 100.0}) = {value} went backwards from {previous}");
            previous = value;
        }
    }

    /// <summary>It is an ease-out: most of the distance is covered early.</summary>
    [Fact]
    public void Front_loads_the_motion() => Assert.True(Easing.Standard(0.5) > 0.9);
}
