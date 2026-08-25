using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NetTrans.Media;

/// <summary>
/// AES-128 segment decryption, which is the only HLS encryption a downloader
/// can do on its own.
///
/// Each segment is a complete AES-128-CBC message with PKCS#7 padding. The IV
/// is either given by the #EXT-X-KEY tag or, when it is not, the segment's
/// media sequence number as a 128-bit big-endian integer -- which is why the
/// sequence number has to survive parsing intact.
/// </summary>
public sealed class HlsDecryptor : IDisposable
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The IV for a segment: the explicit one, or its sequence number.</summary>
    public static byte[] InitialisationVector(HlsSegment segment)
    {
        if (segment.Key?.Iv is { Length: 16 } explicitIv) return explicitIv;

        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), segment.SequenceNumber);
        return iv;
    }

    /// <summary>
    /// Decrypts one segment. <paramref name="key"/> is the 16 bytes fetched
    /// from the key URI.
    /// </summary>
    public static byte[] Decrypt(byte[] cipherText, byte[] key, byte[] iv)
    {
        if (key.Length != 16) throw new CryptographicException($"AES-128 需要 16 字节密钥，实际 {key.Length} 字节。");

        using var aes = Aes.Create();
        aes.Key = key;

        return aes.DecryptCbc(cipherText, iv, PaddingMode.PKCS7);
    }

    /// <summary>
    /// Fetches a key, remembering it. One playlist usually points every segment
    /// at the same key URI, and re-fetching it per segment would be thousands
    /// of pointless requests.
    /// </summary>
    public async Task<byte[]> KeyAsync(Uri keyUri, Net.IHttpTransport transport, CancellationToken cancellationToken)
    {
        string cacheKey = keyUri.AbsoluteUri;

        lock (_keys)
        {
            if (_keys.TryGetValue(cacheKey, out var cached)) return cached;
        }

        // Serialised so a burst of segments starting together fetches once
        // rather than once each.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_keys)
            {
                if (_keys.TryGetValue(cacheKey, out var cached)) return cached;
            }

            using var stream = await transport
                .OpenAsync(keyUri, 0, null, cancellationToken)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            var key = buffer.ToArray();
            if (key.Length != 16) throw new CryptographicException($"密钥长度应为 16 字节，服务器返回 {key.Length} 字节。");

            lock (_keys) _keys[cacheKey] = key;
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
