namespace NetTrans.Net;

/// <summary>
/// The HTTP surface a transfer needs, kept behind an interface so the transfer
/// loop can be tested against a fake server instead of the network.
/// </summary>
public interface IHttpTransport
{
    /// <summary>Asks the server how long the file is and whether it will serve ranges.</summary>
    Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a read stream over bytes <paramref name="from"/>..<paramref name="to"/>
    /// inclusive. A null <paramref name="to"/> reads to the end.
    /// </summary>
    Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken);
}
