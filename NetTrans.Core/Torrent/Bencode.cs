using System.Text;

namespace NetTrans.Torrent;

/// <summary>A bencoded value: a byte string, an integer, a list, or a dictionary.</summary>
public abstract class BValue
{
    /// <summary>
    /// Where this value sat in the bytes it was decoded from.
    ///
    /// This is not bookkeeping for its own sake: a torrent's info hash is the
    /// SHA-1 of its info dictionary <em>as written in the file</em>, and
    /// re-encoding is not guaranteed to reproduce it byte for byte -- a
    /// non-canonical torrent that a client has to accept anyway would hash
    /// differently and match nothing on any tracker.
    /// </summary>
    public int Start { get; internal set; }

    public int Length { get; internal set; }
}

/// <summary>A byte string. Bencode has no text type; the UTF-8 reading is a convenience.</summary>
public sealed class BString : BValue
{
    public BString(byte[] bytes) => Bytes = bytes;

    public byte[] Bytes { get; }

    public string Text => Encoding.UTF8.GetString(Bytes);

    public override string ToString() => Text;
}

public sealed class BInteger : BValue
{
    public BInteger(long value) => Value = value;

    public long Value { get; }

    public override string ToString() => Value.ToString();
}

public sealed class BList : BValue
{
    public BList(IReadOnlyList<BValue> items) => Items = items;

    public IReadOnlyList<BValue> Items { get; }
}

public sealed class BDictionary : BValue
{
    public BDictionary(IReadOnlyDictionary<string, BValue> entries) => Entries = entries;

    /// <summary>Keys are byte strings in the format; they are ASCII in every key that matters.</summary>
    public IReadOnlyDictionary<string, BValue> Entries { get; }

    public BValue? this[string key] => Entries.TryGetValue(key, out var value) ? value : null;

    public byte[]? Bytes(string key) => (this[key] as BString)?.Bytes;

    public string? Text(string key) => (this[key] as BString)?.Text;

    public long? Number(string key) => (this[key] as BInteger)?.Value;

    public BList? List(string key) => this[key] as BList;

    public BDictionary? Dictionary(string key) => this[key] as BDictionary;
}

/// <summary>Thrown when the bytes are not bencode, with where it went wrong.</summary>
public sealed class BencodeException : Exception
{
    public BencodeException(string message, int position)
        : base($"{message}（位置 {position}）") => Position = position;

    public int Position { get; }
}

/// <summary>
/// Bencode, the format a .torrent is written in: byte strings as
/// <c>4:spam</c>, integers as <c>i42e</c>, lists as <c>l...e</c> and
/// dictionaries as <c>d...e</c>.
///
/// The decoder records where each value sat, because the info hash is taken
/// over the original bytes rather than over a re-encoding.
/// </summary>
public static class Bencode
{
    /// <summary>A torrent big enough to nest this deep is malformed or hostile.</summary>
    private const int MaxDepth = 32;

    public static BValue Decode(byte[] data)
    {
        int position = 0;
        var value = Decode(data, ref position, depth: 0);

        // Trailing bytes are tolerated: some torrents carry padding, and
        // refusing one over bytes nobody reads would be pedantry.
        return value;
    }

    /// <summary>The top-level dictionary, which is what every torrent and tracker reply is.</summary>
    public static BDictionary DecodeDictionary(byte[] data) =>
        Decode(data) as BDictionary ?? throw new BencodeException("顶层不是字典", 0);

    private static BValue Decode(byte[] data, ref int position, int depth)
    {
        if (depth > MaxDepth) throw new BencodeException("嵌套过深", position);
        if (position >= data.Length) throw new BencodeException("数据在此处结束", position);

        int start = position;

        BValue value = data[position] switch
        {
            (byte)'i' => DecodeInteger(data, ref position),
            (byte)'l' => DecodeList(data, ref position, depth),
            (byte)'d' => DecodeDictionary(data, ref position, depth),
            >= (byte)'0' and <= (byte)'9' => DecodeString(data, ref position),
            _ => throw new BencodeException($"无法识别的类型 '{(char)data[position]}'", position),
        };

        value.Start = start;
        value.Length = position - start;
        return value;
    }

    private static BInteger DecodeInteger(byte[] data, ref int position)
    {
        int start = ++position; // past 'i'
        int end = IndexOf(data, (byte)'e', start);

        if (end < 0) throw new BencodeException("整数没有结束符", start);

        string text = Encoding.ASCII.GetString(data, start, end - start);

        // "i-0e" and leading zeros are invalid bencode. A torrent that writes
        // them is one whose info hash we would compute differently from every
        // other client, so it is refused rather than guessed at.
        if (text.Length == 0) throw new BencodeException("整数为空", start);
        if (text is "-0") throw new BencodeException("整数写作 -0", start);
        if (text.Length > 1 && (text[0] == '0' || text.StartsWith("-0", StringComparison.Ordinal)))
        {
            throw new BencodeException("整数有前导零", start);
        }

        if (!long.TryParse(text, out long parsed)) throw new BencodeException($"不是整数：{text}", start);

        position = end + 1;
        return new BInteger(parsed);
    }

    private static BString DecodeString(byte[] data, ref int position)
    {
        int colon = IndexOf(data, (byte)':', position);
        if (colon < 0) throw new BencodeException("字符串没有长度分隔符", position);

        string lengthText = Encoding.ASCII.GetString(data, position, colon - position);

        if (!int.TryParse(lengthText, out int length) || length < 0)
        {
            throw new BencodeException($"不是长度：{lengthText}", position);
        }

        int start = colon + 1;
        if (start + length > data.Length) throw new BencodeException("字符串长度超出数据", position);

        var bytes = new byte[length];
        Array.Copy(data, start, bytes, 0, length);

        position = start + length;
        return new BString(bytes);
    }

    private static BList DecodeList(byte[] data, ref int position, int depth)
    {
        position++; // past 'l'
        var items = new List<BValue>();

        while (true)
        {
            if (position >= data.Length) throw new BencodeException("列表没有结束符", position);
            if (data[position] == (byte)'e') break;

            items.Add(Decode(data, ref position, depth + 1));
        }

        position++; // past 'e'
        return new BList(items);
    }

    private static BDictionary DecodeDictionary(byte[] data, ref int position, int depth)
    {
        position++; // past 'd'
        var entries = new Dictionary<string, BValue>(StringComparer.Ordinal);

        while (true)
        {
            if (position >= data.Length) throw new BencodeException("字典没有结束符", position);
            if (data[position] == (byte)'e') break;

            if (data[position] is < (byte)'0' or > (byte)'9')
            {
                throw new BencodeException("字典的键必须是字符串", position);
            }

            var key = DecodeString(data, ref position);
            var value = Decode(data, ref position, depth + 1);

            // A repeated key is invalid; keeping the first matches what the
            // reference implementation does when it meets one.
            entries.TryAdd(key.Text, value);
        }

        position++; // past 'e'
        return new BDictionary(entries);
    }

    private static int IndexOf(byte[] data, byte needle, int from)
    {
        for (int i = from; i < data.Length; i++)
        {
            if (data[i] == needle) return i;
        }

        return -1;
    }

    // ── encoding ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a value back out. Used for the tracker's own replies in tests and
    /// for the extension messages a peer exchange needs -- never to reproduce a
    /// torrent's info dictionary, which is hashed from its original bytes.
    /// </summary>
    public static byte[] Encode(BValue value)
    {
        var buffer = new MemoryStream();
        Write(buffer, value);
        return buffer.ToArray();
    }

    private static void Write(Stream output, BValue value)
    {
        switch (value)
        {
            case BString text:
                WriteAscii(output, text.Bytes.Length.ToString());
                output.WriteByte((byte)':');
                output.Write(text.Bytes, 0, text.Bytes.Length);
                break;

            case BInteger number:
                output.WriteByte((byte)'i');
                WriteAscii(output, number.Value.ToString());
                output.WriteByte((byte)'e');
                break;

            case BList list:
                output.WriteByte((byte)'l');
                foreach (var item in list.Items) Write(output, item);
                output.WriteByte((byte)'e');
                break;

            case BDictionary dictionary:
                output.WriteByte((byte)'d');

                // Keys are written in sorted byte order, which is what the
                // format requires and what makes an encoding reproducible.
                foreach (var entry in dictionary.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    Write(output, new BString(Encoding.UTF8.GetBytes(entry.Key)));
                    Write(output, entry.Value);
                }

                output.WriteByte((byte)'e');
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.GetType().Name);
        }
    }

    private static void WriteAscii(Stream output, string text)
    {
        foreach (char c in text) output.WriteByte((byte)c);
    }

    /// <summary>Builders, so callers can put a message together without the ceremony.</summary>
    public static BString String(string text) => new(Encoding.UTF8.GetBytes(text));

    public static BString String(byte[] bytes) => new(bytes);

    public static BInteger Number(long value) => new(value);

    public static BList List(params BValue[] items) => new(items);

    public static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new(entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
}
