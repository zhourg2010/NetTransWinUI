using NetTrans.Verify;

namespace NetTrans.Services;

/// <summary>
/// The 哈希库 on disk: NetTrans.hashes.json next to the executable, or under
/// LocalAppData when that directory is read-only.
///
/// Loaded the first time something asks for it. A person who turns 哈希库 off
/// should not have the file read at every start, and the first read only ever
/// happens after a download has finished.
/// </summary>
public sealed class HashDatabaseStore
{
    private readonly Lazy<HashDatabase> _database;
    private readonly string _path;

    public HashDatabaseStore()
    {
        _path = PortableStorage.PathFor("NetTrans.hashes.json", "hashes.json");
        _database = new Lazy<HashDatabase>(() => HashDatabase.Load(_path));
    }

    public HashDatabase Database => _database.Value;

    public string Path => _path;

    /// <summary>
    /// Writes the database if anything changed. Best-effort by design: a full
    /// disk must not turn a finished download into an error.
    /// </summary>
    public void Flush()
    {
        if (!_database.IsValueCreated || !_database.Value.Dirty) return;

        try
        {
            _database.Value.Save(_path);
        }
        catch (Exception)
        {
            // The database is a convenience; losing today's rows is survivable.
        }
    }
}
