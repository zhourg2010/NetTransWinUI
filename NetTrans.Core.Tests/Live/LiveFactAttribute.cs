using Xunit;

namespace NetTrans.Tests.Live;

/// <summary>
/// A test that talks to the real internet.
///
/// Skipped unless NETTRANS_LIVE=1, because a suite that fails when a mirror in
/// another country is down is a suite people learn to ignore. The numbers it
/// produces are a measurement, not a pass mark -- what is asserted is only that
/// our own transport did its job.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string Switch = "NETTRANS_LIVE";

    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
        {
            Skip = $"联网测试默认不跑；设 {Switch}=1 打开";
        }
    }
}
