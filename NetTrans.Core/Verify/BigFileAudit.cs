using NetTrans.Download;
using NetTrans.Net;

namespace NetTrans.Verify;

/// <param name="Skipped">True when the ledger already had this exact file and nothing was read.</param>
public sealed record BigFileOutcome(
    BigFile File,
    VerifyOutcome Verdict,
    string? Sha256,
    string? Expected,
    string? From,
    TimeSpan Took,
    bool Skipped);

/// <summary>Where a run has got to, for a caller that wants to show it.</summary>
public sealed record BigFileProgress(BigFile File, int Index, int Total, long Done);

public sealed record AuditOptions
{
    /// <summary>Hash again even when the ledger says the file has not changed.</summary>
    public bool Rehash { get; init; }

    /// <summary>Ask the site the file came from whether it published a digest.</summary>
    public bool Online { get; init; } = true;
}

/// <summary>
/// 大文件核对: hash every oversized file on this machine, check it against
/// whatever anybody published, and write the result to the ledger.
///
/// The expectation is looked for before the file is read, not after: the local
/// ledger, then 哈希库, then the site the mark of the web says it came from.
/// Knowing first is what makes it possible to check a published MD5 in the same
/// pass that computes the SHA-256 -- a second pass over an 8 GB ISO is a real
/// four minutes of someone's disk.
///
/// Most files will have no expectation at all, and that is the designed
/// outcome, not a failure: the hash is recorded and left to stand. Next month's
/// run compares against it, which is how a bit-rotted archive or a swapped file
/// announces itself.
/// </summary>
public static class BigFileAudit
{
    public static async Task<IReadOnlyList<BigFileOutcome>> RunAsync(
        IEnumerable<BigFile> files,
        BigFileLedger ledger,
        IClock clock,
        HashDatabase? published = null,
        IHttpTransport? transport = null,
        IProgress<BigFileProgress>? progress = null,
        AuditOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AuditOptions();

        var queue = files.ToList();
        var outcomes = new List<BigFileOutcome>(queue.Count);

        for (int i = 0; i < queue.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = queue[i];
            progress?.Report(new BigFileProgress(file, i, queue.Count, 0));

            if (!options.Rehash && ledger.IsCurrent(file) && ledger.Find(file.Path) is { } known)
            {
                outcomes.Add(new BigFileOutcome(file, known.Verdict, known.Sha256, known.Expected, known.ExpectedFrom, TimeSpan.Zero, Skipped: true));
                continue;
            }

            outcomes.Add(await AuditAsync(file, ledger, clock, published, transport, progress, options, i, queue.Count, cancellationToken)
                .ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>Drops ledger rows whose file is no longer on disk. Deleting an ISO should not leave it in the inventory forever.</summary>
    public static int Prune(BigFileLedger ledger) =>
        ledger.Forget(row => !File.Exists(row.Path));

    private static async Task<BigFileOutcome> AuditAsync(
        BigFile file,
        BigFileLedger ledger,
        IClock clock,
        HashDatabase? published,
        IHttpTransport? transport,
        IProgress<BigFileProgress>? progress,
        AuditOptions options,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        string? source = ZoneIdentifier.ReadSource(file.Path);

        var claim = await ExpectAsync(file, ledger, published, options.Online ? transport : null, source, cancellationToken)
            .ConfigureAwait(false);

        var started = clock.UtcNow;

        var kinds = claim is null ? new[] { HashKind.Sha256 } : new[] { HashKind.Sha256, claim.Kind };

        IReadOnlyDictionary<HashKind, string> digests;
        try
        {
            digests = await FileHash.ComputeFileAsync(
                file.Path,
                kinds,
                new Progress<long>(done => progress?.Report(new BigFileProgress(file, index, total, done))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Locked by another process, or unplugged halfway through. Nothing
            // is written for it, so the next run picks it up again.
            return new BigFileOutcome(file, VerifyOutcome.Missing, null, claim?.Hash, claim?.From, clock.UtcNow - started, Skipped: false);
        }

        string sha256 = digests[HashKind.Sha256];
        var took = clock.UtcNow - started;

        var verdict = claim is null ? VerifyOutcome.Recorded
            : FileHash.Matches(digests[claim.Kind], claim.Hash) ? VerifyOutcome.Match
            : claim.Local ? VerifyOutcome.Changed
            : VerifyOutcome.Mismatch;

        ledger.Record(new BigFileRecord
        {
            Path = file.Path,
            Size = file.Size,
            Modified = file.Modified,
            Sha256 = sha256,
            HashedAt = clock.UtcNow,
            Took = took,
            Expected = claim?.Hash,
            ExpectedFrom = claim?.From,
            Source = source,
            Verdict = verdict,
        });

        return new BigFileOutcome(file, verdict, sha256, claim?.Hash, claim?.From, took, Skipped: false);
    }

    private static async Task<Claim?> ExpectAsync(
        BigFile file,
        BigFileLedger ledger,
        HashDatabase? published,
        IHttpTransport? transport,
        string? source,
        CancellationToken cancellationToken)
    {
        // 1. What this same path hashed to last time. The file has changed
        //    since -- that is why we are here -- so this is a comparison, not a
        //    verification, and it is marked as such.
        if (ledger.Find(file.Path) is { Sha256.Length: > 0 } previous)
        {
            return new Claim(HashKind.Sha256, previous.Sha256, "上次扫描", Local: true);
        }

        // 2. 哈希库, read only. A file downloaded through NetTrans and verified
        //    against a published digest already has the answer sitting there.
        if (published?.Lookup(file.Name, file.Size) is { Origin: HashOrigin.Published } known)
        {
            return new Claim(HashKind.Sha256, known.Sha256, known.Source is { Length: > 0 } from ? $"哈希库（{from}）" : "哈希库", Local: false);
        }

        // 3. The site it came from, if the mark of the web still remembers.
        if (transport is null || !Uri.TryCreate(source, UriKind.Absolute, out var url)) return null;

        try
        {
            var found = await OnlineChecksums.LookupAsync(transport, url, file.Name, cancellationToken).ConfigureAwait(false);
            return found is null ? null : new Claim(found.Kind, found.Hash, found.Source.ToString(), Local: false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <param name="Local">True when the claim is our own earlier reading rather than somebody's published digest.</param>
    private sealed record Claim(HashKind Kind, string Hash, string From, bool Local);
}
