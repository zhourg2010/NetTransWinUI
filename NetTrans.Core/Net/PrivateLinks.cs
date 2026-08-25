using System.Text;

namespace NetTrans.Net;

/// <summary>
/// thunder:// flashget:// qqdl:// — the wrappers Chinese download managers put
/// around an ordinary URL.
///
/// None of them is a protocol. Each is base64 of the real address with a couple
/// of marker characters glued on, and sites still publish them because a
/// decade of habit says a download manager will understand. Pasting one and
/// being told "无法识别" is a bad answer when the address is right there inside
/// it.
/// </summary>
public static class PrivateLinks
{
    /// <summary>Whether this is one of the wrapped schemes, decodable or not.</summary>
    public static bool IsWrapped(string? text)
    {
        string trimmed = (text ?? "").Trim();

        return trimmed.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("flashget://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("qqdl://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("fs2you://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The address inside, or the text unchanged when there is nothing to
    /// unwrap.
    ///
    /// Anything that does not decode to a usable address comes back as it went
    /// in: the caller's own "this is not a link" message is a better answer than
    /// a mangled one.
    /// </summary>
    public static string Unwrap(string? text)
    {
        string trimmed = (text ?? "").Trim();
        if (!IsWrapped(trimmed)) return trimmed;

        int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        string payload = trimmed[(scheme + 3)..].Trim();

        // flashget puts &fs=0 (or similar) after the payload.
        int amp = payload.IndexOf('&');
        if (amp >= 0) payload = payload[..amp];

        if (!TryBase64(payload, out string decoded)) return trimmed;

        // thunder wraps as AA<url>ZZ; flashget as [FLASHGET]<url>[FLASHGET].
        decoded = Strip(decoded, "AA", "ZZ");
        decoded = Strip(decoded, "[FLASHGET]", "[FLASHGET]");

        return Usable(decoded) ? decoded : trimmed;
    }

    /// <summary>Every link in a batch, unwrapped where one needs it.</summary>
    public static IEnumerable<string> UnwrapAll(IEnumerable<string> links) => links.Select(Unwrap);

    private static string Strip(string text, string prefix, string suffix)
    {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return text;
        if (text.Length < prefix.Length + suffix.Length) return text;

        return text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..^suffix.Length]
            : text[prefix.Length..];
    }

    private static bool TryBase64(string payload, out string decoded)
    {
        decoded = "";

        // Some sites strip the padding, which Convert refuses outright.
        string padded = payload.Replace('-', '+').Replace('_', '/');
        if (padded.Length % 4 != 0) padded = padded.PadRight(padded.Length + (4 - padded.Length % 4), '=');

        Span<byte> bytes = new byte[padded.Length];

        if (!Convert.TryFromBase64String(padded, bytes, out int written)) return false;

        decoded = Encoding.UTF8.GetString(bytes[..written]).Trim('\0').Trim();
        return decoded.Length > 0;
    }

    private static bool Usable(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var url) &&
        url.Scheme is "http" or "https" or "ftp" or "ftps" or "magnet" or "ed2k";
}
