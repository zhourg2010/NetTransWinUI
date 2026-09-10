using NetTrans.Verify;

namespace NetTrans.Services;

/// <summary>
/// 大文件账本 on disk: NetTrans.bigfiles.json, next to the executable.
///
/// A second file on purpose. 哈希库 (NetTrans.hashes.json) is about files as
/// things that can be downloaded; this is an inventory of one machine's disks,
/// full of VM images and ISOs nobody published a digest for. Keeping them apart
/// is what stops a local scan of a half-written file from becoming "what this
/// release hashes to" for every future download.
/// </summary>
public sealed class BigFileStore
{
    private readonly Lazy<BigFileLedger> _ledger;

    public BigFileStore()
    {
        Path = PortableStorage.PathFor("netX.bigfiles.json", "bigfiles.json");
        _ledger = new Lazy<BigFileLedger>(() => BigFileLedger.Load(Path));
    }

    public string Path { get; }

    /// <summary>The report written beside the ledger, so the last run is readable without the app.</summary>
    public string ReportPath => System.IO.Path.ChangeExtension(Path, ".md");

    public BigFileLedger Ledger => _ledger.Value;

    public void Flush()
    {
        if (!_ledger.IsValueCreated || !_ledger.Value.Dirty) return;

        try
        {
            _ledger.Value.Save(Path);
        }
        catch (Exception)
        {
            // An inventory is a convenience; a full disk must not end the run.
        }
    }
}
