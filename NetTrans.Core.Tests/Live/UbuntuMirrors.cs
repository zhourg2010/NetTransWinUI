namespace NetTrans.Tests.Live;

/// <summary>
/// Ubuntu's archive mirrors, one per region.
///
/// Every one of them serves the same tree, so the same relative path measures
/// the same transfer everywhere -- which is the only way the numbers can be
/// compared. Contents-amd64.gz is used because it is tens of megabytes on
/// every mirror and is not an ISO nobody wanted to fetch.
/// </summary>
public static class UbuntuMirrors
{
    public const string Path = "dists/noble/Contents-amd64.gz";

    public static IReadOnlyList<(string Name, string Root)> Roots { get; } = new[]
    {
        ("英国 · 官方", "http://archive.ubuntu.com/ubuntu/"),
        ("美国", "http://us.archive.ubuntu.com/ubuntu/"),
        ("德国", "http://de.archive.ubuntu.com/ubuntu/"),
        ("日本", "http://jp.archive.ubuntu.com/ubuntu/"),
        ("韩国", "http://kr.archive.ubuntu.com/ubuntu/"),
        ("新加坡", "http://sg.archive.ubuntu.com/ubuntu/"),
        ("巴西", "http://br.archive.ubuntu.com/ubuntu/"),
        ("澳大利亚 · AARNet", "https://mirror.aarnet.edu.au/pub/ubuntu/archive/"),
        ("中国 · 官方", "http://cn.archive.ubuntu.com/ubuntu/"),
        ("中国 · 清华 TUNA", "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/"),
        ("中国 · 阿里云", "https://mirrors.aliyun.com/ubuntu/"),
        ("中国 · 中科大", "https://mirrors.ustc.edu.cn/ubuntu/"),
    };

    public static IEnumerable<(string Name, Uri Url)> All() =>
        Roots.Select(mirror => (mirror.Name, new Uri(new Uri(mirror.Root), Path)));
}
