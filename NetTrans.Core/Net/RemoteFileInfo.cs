namespace NetTrans.Net;

/// <summary>
/// What a probe learned about a URL before any bytes are moved: how long the
/// file is, whether the server will serve ranges (so the transfer can be split
/// and resumed), and what to call it on disk.
/// </summary>
/// <param name="Length">Size in bytes, or -1 when the server did not say.</param>
/// <param name="SupportsRanges">True when the server answered a range request with 206.</param>
/// <param name="ETag">Validator used to decide whether a half-finished file may be resumed.</param>
/// <param name="LastModified">Fallback validator when there is no ETag.</param>
/// <param name="FileName">From Content-Disposition, else the last path segment.</param>
/// <param name="ContentType">Reported MIME type, if any.</param>
public sealed record RemoteFileInfo(
    long Length,
    bool SupportsRanges,
    string? ETag,
    string? LastModified,
    string FileName,
    string? ContentType = null)
{
    public bool HasKnownLength => Length > 0;

    /// <summary>A transfer can only be split when the server will serve ranges out of a known-length file.</summary>
    public bool CanSplit => HasKnownLength && SupportsRanges;
}
