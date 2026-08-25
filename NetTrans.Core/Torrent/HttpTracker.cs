using NetTrans.Net;

namespace NetTrans.Torrent;

/// <summary>
/// The original tracker protocol: a GET with the request in the query string
/// and a bencoded dictionary back.
///
/// It goes through <see cref="IHttpTransport"/> rather than its own HttpClient
/// so it shares the app's one connection pool and user agent -- and so a test
/// can answer it without a server.
/// </summary>
public sealed class HttpTrackerClient : ITrackerClient
{
    /// <summary>A tracker reply is a short dictionary; anything larger is not one.</summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly IHttpTransport _transport;

    public HttpTrackerClient(IHttpTransport transport) => _transport = transport;

    public bool CanAnnounceTo(Uri tracker) => tracker.Scheme is "http" or "https";

    public async Task<AnnounceResponse> AnnounceAsync(
        Uri tracker,
        AnnounceRequest request,
        CancellationToken cancellationToken)
    {
        var url = TrackerProtocol.BuildQuery(tracker, request);

        byte[] body;

        try
        {
            await using var stream = await _transport.OpenAsync(url, 0, null, cancellationToken).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];

            while (buffer.Length < MaxResponseBytes)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TrackerException)
        {
            throw new TrackerException($"无法连接 tracker {tracker.Host}：{exception.Message}", exception);
        }

        return TrackerProtocol.ParseResponse(body);
    }
}
