namespace NetTrans.Net;

/// <summary>
/// The mark of the web: the small INI Windows keeps in a downloaded file's
/// <c>Zone.Identifier</c> alternate stream. It is what makes SmartScreen warn
/// before an installer runs and what makes Office open a document in Protected
/// View, so writing it is the single most useful thing 完成后扫描 does -- an
/// on-demand scan only sees what its signatures already know, while the mark
/// keeps working after the file has been moved or copied.
/// </summary>
public static class ZoneIdentifier
{
    /// <summary>URLZONE_INTERNET. The one that makes Windows treat a file as untrusted.</summary>
    public const int InternetZone = 3;

    /// <summary>
    /// The stream's contents for a file fetched from <paramref name="url"/>.
    /// CRLF, because this is read by Windows components that expect an INI.
    /// </summary>
    public static string Build(string? url)
    {
        var lines = new List<string> { "[ZoneTransfer]", $"ZoneId={InternetZone}" };

        // Only http(s) belongs here -- the fields are URLs, and a file:// or
        // magnet: source says nothing a zone check can use.
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            lines.Add($"HostUrl={parsed.AbsoluteUri}");

            // The referrer is conventionally the site rather than the file, and
            // it is what the "解除锁定" dialog shows as the source.
            lines.Add($"ReferrerUrl={parsed.GetLeftPart(UriPartial.Authority)}/");
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The alternate stream path for a file. Windows only; NTFS only.</summary>
    public static string StreamPath(string filePath) => filePath + ":Zone.Identifier";
}
