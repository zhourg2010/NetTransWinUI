namespace NetTrans.Models;

public sealed class MirrorSource
{
    public required string Region { get; init; }
    public required string Host { get; init; }
    public double Speed { get; init; }
    public int Share { get; init; }
}
