using NetTrans.Services;

namespace NetTrans.Verify;

/// <summary>Turns a 大文件核对 run into the two things a person wants: a verdict line and a table.</summary>
public static class BigFileReport
{
    public static string Describe(VerifyOutcome outcome) => outcome switch
    {
        VerifyOutcome.Match => "已核对",
        VerifyOutcome.Mismatch => "对不上",
        VerifyOutcome.Changed => "变了",
        VerifyOutcome.Missing => "读不了",
        _ => "已记录",
    };

    /// <summary>The line that says whether anything needs attention.</summary>
    public static string Summary(IReadOnlyList<BigFileOutcome> outcomes)
    {
        if (outcomes.Count == 0) return "没有找到超过门槛的文件。";

        long bytes = outcomes.Sum(outcome => outcome.File.Size);
        int hashed = outcomes.Count(outcome => !outcome.Skipped && outcome.Sha256 is not null);
        int skipped = outcomes.Count(outcome => outcome.Skipped);
        int checked_ = outcomes.Count(outcome => outcome.Verdict == VerifyOutcome.Match);
        int wrong = outcomes.Count(outcome => outcome.Verdict is VerifyOutcome.Mismatch or VerifyOutcome.Changed);
        int unread = outcomes.Count(outcome => outcome.Verdict == VerifyOutcome.Missing);

        var parts = new List<string>
        {
            $"{outcomes.Count} 个文件，共 {FormatHelpers.Bytes(bytes)}",
            $"本次算了 {hashed} 个",
        };

        if (skipped > 0) parts.Add($"{skipped} 个没变、跳过");
        if (checked_ > 0) parts.Add($"{checked_} 个和发布值一致");
        if (unread > 0) parts.Add($"{unread} 个读不了");

        parts.Add(wrong > 0 ? $"⚠ {wrong} 个对不上，见下表" : "没有对不上的");

        return string.Join("，", parts) + "。";
    }

    /// <summary>Biggest first. Anything that failed is pulled to the top, because that is what the table is for.</summary>
    public static string Table(IReadOnlyList<BigFileOutcome> outcomes)
    {
        var lines = new List<string>
        {
            "| 文件 | 大小 | SHA-256 | 结论 | 比对来源 |",
            "| --- | ---: | --- | --- | --- |",
        };

        var ordered = outcomes
            .OrderByDescending(outcome => outcome.Verdict is VerifyOutcome.Mismatch or VerifyOutcome.Changed)
            .ThenByDescending(outcome => outcome.File.Size);

        foreach (var outcome in ordered)
        {
            string hash = outcome.Sha256 is { Length: > 16 } full ? full[..16] + "…" : outcome.Sha256 ?? "—";

            lines.Add($"| {outcome.File.Name} | {FormatHelpers.Bytes(outcome.File.Size)} | `{hash}` | " +
                      $"{Describe(outcome.Verdict)} | {outcome.From ?? "—"} |");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The whole thing, for a file on disk or a CI summary.</summary>
    public static string Full(IReadOnlyList<BigFileOutcome> outcomes, IEnumerable<string> roots, long minimumSize) =>
        string.Join('\n', new[]
        {
            $"## 大文件核对（≥ {FormatHelpers.Bytes(minimumSize)}）",
            "",
            $"扫描范围：{string.Join("、", roots)}",
            "",
            Summary(outcomes),
            "",
            Table(outcomes),
            "",
            "> 页面文件、休眠文件、崩溃转储和未下载完的文件不在其中：它们在被读的同时还在变，算出来的值一落地就是错的。",
        });
}
