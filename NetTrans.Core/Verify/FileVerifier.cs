using NetTrans.Download;
using NetTrans.Net;

namespace NetTrans.Verify;

public enum VerifyOutcome
{
    /// <summary>The file was not there to hash.</summary>
    Missing,

    /// <summary>Hashed and matched what it was supposed to be.</summary>
    Match,

    /// <summary>Hashed and did not match a digest somebody published. The file is wrong.</summary>
    Mismatch,

    /// <summary>Differs from what we ourselves recorded for this name and length last time. Suspicious, not proof.</summary>
    Changed,

    /// <summary>Nothing to compare against; the digest went into the database for next time.</summary>
    Recorded,
}

/// <param name="Status">The 校验 row's text.</param>
/// <param name="Detail">The line written to the task's log.</param>
public sealed record VerifyReport(
    VerifyOutcome Outcome,
    string? Sha256,
    string? Expected,
    HashKind Kind,
    HashOrigin Source,
    string? From,
    string Status,
    string Detail)
{
    public bool IsError => Outcome is VerifyOutcome.Mismatch or VerifyOutcome.Changed;
}

/// <summary>
/// 完成后校验, in full: find out what the file is supposed to hash to, hash it,
/// and say whether it does.
///
/// The expectation is looked for in cost order -- what the task already carries,
/// then the local 哈希库, then the server. A file that has been downloaded
/// before is therefore verified without a single request, and a file nobody
/// publishes a digest for still gets its hash recorded, which is what makes the
/// second download checkable.
/// </summary>
public static class FileVerifier
{
    public static async Task<VerifyReport> VerifyAsync(
        string path,
        string? url = null,
        string? expected = null,
        HashDatabase? database = null,
        IHttpTransport? transport = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new VerifyReport(
                VerifyOutcome.Missing, null, null, HashKind.Sha256, HashOrigin.Computed, null,
                Status: FileHash.Pending, Detail: "校验跳过：文件不在了");
        }

        string name = file.Name;
        long size = file.Length;

        var claim = await ExpectAsync(name, size, url, expected, database, transport, cancellationToken).ConfigureAwait(false);

        var kinds = claim is null ? new[] { HashKind.Sha256 } : new[] { HashKind.Sha256, claim.Kind };
        var digests = await FileHash.ComputeFileAsync(path, kinds, progress, cancellationToken).ConfigureAwait(false);

        string sha256 = digests[HashKind.Sha256];

        if (claim is null)
        {
            database?.Remember(sha256, size, name, HashOrigin.Computed, url);

            return new VerifyReport(
                VerifyOutcome.Recorded, sha256, null, HashKind.Sha256, HashOrigin.Computed, null,
                Status: FileHash.Describe(sha256, expected: null),
                Detail: $"SHA-256 {sha256}（已记入哈希库）");
        }

        bool ok = FileHash.Matches(digests[claim.Kind], claim.Hash);

        Record(database, claim, sha256, size, name, url, ok);

        // A published digest that does not match means the file is wrong. Our
        // own earlier reading not matching means the file is different from
        // last time, which is worth saying but is not the same accusation.
        var outcome = ok ? VerifyOutcome.Match
            : claim.Origin == HashOrigin.Computed ? VerifyOutcome.Changed
            : VerifyOutcome.Mismatch;

        return new VerifyReport(
            outcome,
            sha256,
            claim.Hash,
            claim.Kind,
            claim.Origin,
            claim.From,
            Status: Status(outcome, sha256, claim),
            Detail: Detail(outcome, digests[claim.Kind], claim));
    }

    /// <summary>What the file is supposed to hash to, and who says so.</summary>
    private static async Task<Claim?> ExpectAsync(
        string name,
        long size,
        string? url,
        string? expected,
        HashDatabase? database,
        IHttpTransport? transport,
        CancellationToken cancellationToken)
    {
        // 1. Whatever came with the task. Free, and the most specific thing anyone told us.
        if (!string.IsNullOrWhiteSpace(expected))
        {
            string hash = new(expected.Trim().TakeWhile(Uri.IsHexDigit).ToArray());
            if (HashKinds.TryFromHexLength(hash.Length, out var given))
            {
                return new Claim(given, hash.ToLowerInvariant(), HashOrigin.Given, "任务自带的校验值");
            }
        }

        // 2. The local database. Also free, and covers everything downloaded before.
        if (database?.Lookup(name, size) is { } known)
        {
            return new Claim(
                HashKind.Sha256,
                known.Sha256,
                known.Origin,
                known.Origin == HashOrigin.Published ? $"哈希库（{Shorten(known.Source)} 发布）" : "哈希库（上次下载）");
        }

        // 3. The server. One round of requests, all of which may 404.
        if (transport is not null && Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            OnlineChecksum? found;
            try
            {
                found = await OnlineChecksums.LookupAsync(transport, address, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                found = null;
            }

            if (found is not null)
            {
                return new Claim(found.Kind, found.Hash, HashOrigin.Published, $"{Shorten(found.Source.ToString())}", found.Source.ToString());
            }
        }

        return null;
    }

    private static void Record(HashDatabase? database, Claim claim, string sha256, long size, string name, string? url, bool matched)
    {
        if (database is null) return;

        // A published SHA-256 is worth storing whether or not this copy matched
        // it: it is a fact about the file, and the next attempt should be
        // checked against it without going back to the server.
        if (claim.Origin == HashOrigin.Published && claim.Kind == HashKind.Sha256)
        {
            database.Remember(claim.Hash, size, name, HashOrigin.Published, url, claim.Source);
            return;
        }

        if (!matched) return;

        // Matched a published MD5 or a digest the task carried: the file is
        // confirmed, so its SHA-256 can be stored with that same confidence.
        database.Remember(sha256, size, name, claim.Origin, url, claim.Source);
    }

    private static string Status(VerifyOutcome outcome, string sha256, Claim claim) => outcome switch
    {
        VerifyOutcome.Match => claim.Kind == HashKind.Sha256 ? FileHash.Verified : $"{claim.Kind.Label()} 已校验",
        VerifyOutcome.Changed => $"{claim.Kind.Label()} 与上次不一致",
        VerifyOutcome.Mismatch => claim.Kind == HashKind.Sha256 ? FileHash.Mismatch : $"{claim.Kind.Label()} 校验失败",
        _ => FileHash.Describe(sha256, expected: null),
    };

    private static string Detail(VerifyOutcome outcome, string computed, Claim claim) => outcome switch
    {
        VerifyOutcome.Match => $"{claim.Kind.Label()} 已校验 · 来自{claim.From}",
        VerifyOutcome.Changed => $"{claim.Kind.Label()} 与哈希库不一致：上次 {Head(claim.Hash)}，这次 {Head(computed)}",
        _ => $"{claim.Kind.Label()} 校验失败：应为 {Head(claim.Hash)}，实为 {Head(computed)}（来自{claim.From}）",
    };

    private static string Head(string hash) => hash.Length <= 16 ? hash : hash[..16] + "…";

    /// <summary>A checksum file's URL is only ever shown as "where this came from"; the host and last segment say that.</summary>
    private static string Shorten(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "服务器";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return source;

        string last = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : "";
        return last.Length > 0 ? $"{uri.Host}/{last}" : uri.Host;
    }

    private sealed record Claim(HashKind Kind, string Hash, HashOrigin Origin, string From, string? Source = null);
}
