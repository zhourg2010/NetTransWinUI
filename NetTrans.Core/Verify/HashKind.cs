using System.Security.Cryptography;

namespace NetTrans.Verify;

/// <summary>
/// The digests a published checksum file may carry. Sites publish whatever
/// they published years ago, so a verifier that only understands SHA-256
/// silently declines to check half the downloads on the web.
/// </summary>
public enum HashKind
{
    Md5 = 1,
    Sha1 = 2,
    Sha256 = 3,
    Sha512 = 4,
}

public static class HashKinds
{
    /// <summary>How many hex characters this digest is written as. The only reliable way to tell one apart in a checksum file.</summary>
    public static int HexLength(this HashKind kind) => kind switch
    {
        HashKind.Md5 => 32,
        HashKind.Sha1 => 40,
        HashKind.Sha256 => 64,
        HashKind.Sha512 => 128,
        _ => 0,
    };

    public static string Label(this HashKind kind) => kind switch
    {
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA-1",
        HashKind.Sha256 => "SHA-256",
        HashKind.Sha512 => "SHA-512",
        _ => "?",
    };

    /// <summary>Recognises a digest by its length, which is what a bare line of hex gives us.</summary>
    public static bool TryFromHexLength(int length, out HashKind kind)
    {
        kind = length switch
        {
            32 => HashKind.Md5,
            40 => HashKind.Sha1,
            64 => HashKind.Sha256,
            128 => HashKind.Sha512,
            _ => 0,
        };

        return kind != 0;
    }

    /// <summary>Reads the algorithm name a BSD-style line starts with ("SHA256", "sha-256", "MD5").</summary>
    public static bool TryParse(string? name, out HashKind kind)
    {
        kind = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string cleaned = new(name.Where(char.IsLetterOrDigit).ToArray());

        kind = cleaned.ToUpperInvariant() switch
        {
            "MD5" => HashKind.Md5,
            "SHA1" => HashKind.Sha1,
            "SHA256" => HashKind.Sha256,
            "SHA512" => HashKind.Sha512,
            _ => 0,
        };

        return kind != 0;
    }

    internal static HashAlgorithm Create(this HashKind kind) => kind switch
    {
        HashKind.Md5 => MD5.Create(),
        HashKind.Sha1 => SHA1.Create(),
        HashKind.Sha256 => SHA256.Create(),
        HashKind.Sha512 => SHA512.Create(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
