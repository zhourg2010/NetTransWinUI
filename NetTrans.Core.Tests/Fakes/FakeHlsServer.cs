using System.Security.Cryptography;
using System.Text;
using NetTrans.Net;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// A whole HLS tree in a box: a master playlist, a media playlist per
/// rendition, and the segments behind it. Serves anything put into
/// <see cref="Files"/> by absolute URL.
/// </summary>
public sealed class FakeHlsServer : IHttpTransport
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Every URL asked for, so a test can assert what was fetched and how often.</summary>
    public List<string> Requests { get; } = new();

    /// <summary>Fails the next open of these URLs once each, for the retry path.</summary>
    public HashSet<string> FailOnce { get; } = new(StringComparer.Ordinal);

    /// <summary>Awaited before every open, so a test can hold a transfer open.</summary>
    public Func<Task>? BeforeOpen { get; set; }

    public void Add(string url, string text) => Files[url] = Encoding.UTF8.GetBytes(text);

    public void Add(string url, byte[] bytes) => Files[url] = bytes;

    /// <summary>Serves <paramref name="count"/> TS segments of recognisable content, and lists them.</summary>
    public string AddSegments(string baseUrl, int count, int bytesEach = 512, byte[]? key = null, byte[]? iv = null)
    {
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");

        if (key is not null)
        {
            Files[baseUrl + "key.bin"] = key;
            playlist.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"");
            playlist.Append(iv is null ? "\n" : $",IV=0x{Convert.ToHexString(iv)}\n");
        }

        for (int i = 0; i < count; i++)
        {
            // Each segment is filled with its own index, so a file assembled out
            // of order is obvious rather than merely wrong.
            var content = Enumerable.Repeat((byte)i, bytesEach).ToArray();

            Files[$"{baseUrl}seg-{i}.ts"] = key is null
                ? content
                : Encrypt(content, key, iv ?? Iv(i));

            playlist.Append($"#EXTINF:4.0,\nseg-{i}.ts\n");
        }

        playlist.Append("#EXT-X-ENDLIST\n");
        return playlist.ToString();
    }

    /// <summary>What the assembled file should look like for <paramref name="count"/> segments.</summary>
    public static byte[] Expected(int count, int bytesEach = 512) =>
        Enumerable.Range(0, count).SelectMany(i => Enumerable.Repeat((byte)i, bytesEach)).ToArray();

    public static byte[] Iv(long sequence)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
    }

    public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        string key = url.AbsoluteUri;
        long length = Files.TryGetValue(key, out var bytes) ? bytes.Length : 0;

        return Task.FromResult(new RemoteFileInfo(length, true, null, null, "playlist.m3u8"));
    }

    public async Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
    {
        string key = url.AbsoluteUri;
        lock (Requests) Requests.Add(key);

        if (BeforeOpen is { } gate) await gate().WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (Requests)
        {
            if (FailOnce.Remove(key)) throw new IOException($"连接被重置：{key}");
        }

        if (!Files.TryGetValue(key, out var bytes))
        {
            throw new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound);
        }

        // #EXT-X-BYTERANGE asks for a slice of one file.
        if (from > 0 || to is not null)
        {
            long last = Math.Min(to ?? bytes.Length - 1, bytes.Length - 1);
            if (from > last) return new MemoryStream(Array.Empty<byte>());

            return new MemoryStream(bytes.AsSpan((int)from, (int)(last - from + 1)).ToArray());
        }

        return new MemoryStream(bytes);
    }
}
