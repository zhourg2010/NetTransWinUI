using NetTrans.Services;

namespace NetTrans.Net;

/// <summary>Turns 测速 samples into something a person reads: fastest first, and a table.</summary>
public static class SpeedReport
{
    /// <summary>Fastest first; mirrors that failed go last, in the order they were tried.</summary>
    public static IReadOnlyList<SpeedSample> Rank(IEnumerable<SpeedSample> samples)
    {
        var all = samples.ToList();

        return all
            .Select((sample, index) => (sample, index))
            .OrderByDescending(entry => entry.sample.Ok)
            .ThenByDescending(entry => entry.sample.BytesPerSecond)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.sample)
            .ToList();
    }

    /// <summary>A markdown table, which is what a CI summary and a terminal both render legibly.</summary>
    public static string Table(IEnumerable<SpeedSample> samples)
    {
        var lines = new List<string>
        {
            "| 节点 | 速度 | 建立连接 | 已读 | 备注 |",
            "| --- | ---: | ---: | ---: | --- |",
        };

        foreach (var sample in Rank(samples))
        {
            string speed = sample.Ok ? FormatHelpers.Speed(sample.BytesPerSecond) : "—";
            string latency = sample.Connect > TimeSpan.Zero ? $"{sample.Connect.TotalMilliseconds:F0} ms" : "—";

            lines.Add($"| {sample.Name} | {speed} | {latency} | {FormatHelpers.Bytes(sample.Bytes)} | {sample.Error ?? ""} |");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The one-line verdict: who won, and by how much over the median.</summary>
    public static string Verdict(IEnumerable<SpeedSample> samples)
    {
        var ranked = Rank(samples).Where(sample => sample.Ok).ToList();
        if (ranked.Count == 0) return "没有一个节点读到数据";

        var fastest = ranked[0];
        var median = ranked[ranked.Count / 2];

        return median.BytesPerSecond > 0 && ranked.Count > 1
            ? $"最快 {fastest.Name} {FormatHelpers.Speed(fastest.BytesPerSecond)}，是中位数的 {fastest.BytesPerSecond / median.BytesPerSecond:F1} 倍"
            : $"最快 {fastest.Name} {FormatHelpers.Speed(fastest.BytesPerSecond)}";
    }
}
