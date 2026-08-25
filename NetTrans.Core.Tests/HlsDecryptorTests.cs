using System.Security.Cryptography;
using System.Text;
using NetTrans.Media;
using Xunit;

namespace NetTrans.Tests;

/// <summary>AES-128 segments: the IV rule, the round trip, and the key cache.</summary>
public class HlsDecryptorTests
{
    private static readonly Uri KeyUri = new("https://cdn.test/hls/key.bin");

    [Fact]
    public void An_explicit_iv_is_used_as_given()
    {
        var iv = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
        var segment = Segment(sequence: 99, iv);

        Assert.Equal(iv, HlsDecryptor.InitialisationVector(segment));
    }

    [Fact]
    public void Without_one_the_iv_is_the_sequence_number_big_endian()
    {
        // RFC 8216: the media sequence number as a 128-bit big-endian integer.
        var iv = HlsDecryptor.InitialisationVector(Segment(sequence: 1, iv: null));

        Assert.Equal(16, iv.Length);
        Assert.All(iv[..15], b => Assert.Equal(0, b));
        Assert.Equal(1, iv[15]);
    }

    [Fact]
    public void A_larger_sequence_number_spreads_across_the_low_bytes()
    {
        var iv = HlsDecryptor.InitialisationVector(Segment(sequence: 0x0102, iv: null));

        Assert.Equal(0x01, iv[14]);
        Assert.Equal(0x02, iv[15]);
    }

    [Fact]
    public void An_iv_of_the_wrong_length_is_ignored_rather_than_used_short()
    {
        // M3U8 refuses to parse one, but a segment built by hand must not slip
        // a 4-byte IV into AES either.
        var segment = Segment(sequence: 7, iv: new byte[] { 1, 2, 3, 4 });

        var iv = HlsDecryptor.InitialisationVector(segment);
        Assert.Equal(16, iv.Length);
        Assert.Equal(7, iv[15]);
    }

    [Fact]
    public void A_segment_encrypted_the_way_hls_does_it_comes_back_whole()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var segment = Segment(sequence: 42, iv: null);
        var iv = HlsDecryptor.InitialisationVector(segment);

        // Not a round number of blocks, so the PKCS#7 padding is exercised.
        var plain = Encoding.UTF8.GetBytes(new string('x', 1000) + "tail");

        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);

        Assert.Equal(plain, HlsDecryptor.Decrypt(cipher, key, iv));
    }

    [Fact]
    public void A_key_of_the_wrong_size_is_refused_with_a_readable_reason()
    {
        var error = Assert.Throws<CryptographicException>(
            () => HlsDecryptor.Decrypt(new byte[16], new byte[8], new byte[16]));

        Assert.Contains("16 字节", error.Message);
    }

    [Fact]
    public async Task A_key_is_fetched_once_however_many_segments_ask_for_it()
    {
        var transport = new KeyServer(RandomNumberGenerator.GetBytes(16));
        using var decryptor = new HlsDecryptor();

        var keys = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => decryptor.KeyAsync(KeyUri, transport, CancellationToken.None)));

        Assert.Equal(1, transport.Requests);
        Assert.All(keys, key => Assert.Equal(keys[0], key));
    }

    [Fact]
    public async Task A_server_that_returns_the_wrong_number_of_bytes_is_refused()
    {
        var transport = new KeyServer(new byte[8]);
        using var decryptor = new HlsDecryptor();

        var error = await Assert.ThrowsAsync<CryptographicException>(
            () => decryptor.KeyAsync(KeyUri, transport, CancellationToken.None));

        Assert.Contains("16 字节", error.Message);
    }

    private static HlsSegment Segment(long sequence, byte[]? iv) => new(
        new Uri("https://cdn.test/hls/seg.ts"),
        Duration: 4,
        SequenceNumber: sequence,
        Key: new HlsKey(HlsEncryption.Aes128, KeyUri, iv));

    /// <summary>Serves one key and counts how often it was asked for.</summary>
    private sealed class KeyServer : NetTrans.Net.IHttpTransport
    {
        private readonly byte[] _key;
        private int _requests;

        public KeyServer(byte[] key) => _key = key;

        public int Requests => _requests;

        public Task<NetTrans.Net.RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new NetTrans.Net.RemoteFileInfo(_key.Length, true, null, null, "key.bin"));

        public async Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            // A real fetch is not instant, which is what makes a burst of
            // callers race for the cache in the first place.
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);

            return new MemoryStream(_key);
        }
    }
}
