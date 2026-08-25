using System.Text;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Bencode. The part that has to be exact is where each value sat: a torrent's
/// info hash is taken over the original bytes, so a decoder that loses them
/// gives a hash no tracker recognises.
/// </summary>
public class BencodeTests
{
    [Fact]
    public void Byte_strings_carry_their_length()
    {
        var value = Assert.IsType<BString>(Bencode.Decode(Raw("4:spam")));
        Assert.Equal("spam", value.Text);
    }

    [Fact]
    public void An_empty_string_is_a_string() =>
        Assert.Empty(Assert.IsType<BString>(Bencode.Decode(Raw("0:"))).Bytes);

    [Fact]
    public void A_string_can_hold_bytes_that_are_not_text()
    {
        // Piece hashes are raw SHA-1s, so this is the normal case, not an edge.
        var data = Raw("3:").Concat(new byte[] { 0x00, 0xFF, 0x80 }).ToArray();

        Assert.Equal(new byte[] { 0x00, 0xFF, 0x80 }, Assert.IsType<BString>(Bencode.Decode(data)).Bytes);
    }

    [Theory]
    [InlineData("i42e", 42)]
    [InlineData("i0e", 0)]
    [InlineData("i-42e", -42)]
    [InlineData("i9223372036854775807e", long.MaxValue)]
    public void Integers_are_read(string text, long expected) =>
        Assert.Equal(expected, Assert.IsType<BInteger>(Bencode.Decode(Raw(text))).Value);

    [Theory]
    [InlineData("i-0e")]
    [InlineData("i03e")]
    [InlineData("i-03e")]
    [InlineData("ie")]
    public void An_integer_written_the_invalid_way_is_refused(string text)
    {
        // Not pedantry: a torrent whose integers we read differently from every
        // other client is one whose info hash matches nothing.
        Assert.Throws<BencodeException>(() => Bencode.Decode(Raw(text)));
    }

    [Fact]
    public void Lists_hold_values_in_order()
    {
        var list = Assert.IsType<BList>(Bencode.Decode(Raw("l4:spami42ee")));

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("spam", ((BString)list.Items[0]).Text);
        Assert.Equal(42, ((BInteger)list.Items[1]).Value);
    }

    [Fact]
    public void An_empty_list_and_an_empty_dictionary_are_valid()
    {
        Assert.Empty(Assert.IsType<BList>(Bencode.Decode(Raw("le"))).Items);
        Assert.Empty(Assert.IsType<BDictionary>(Bencode.Decode(Raw("de"))).Entries);
    }

    [Fact]
    public void Dictionaries_are_read_by_key()
    {
        var dictionary = Bencode.DecodeDictionary(Raw("d3:cow3:moo4:spam4:eggse"));

        Assert.Equal("moo", dictionary.Text("cow"));
        Assert.Equal("eggs", dictionary.Text("spam"));
        Assert.Null(dictionary.Text("missing"));
    }

    [Fact]
    public void Nesting_works()
    {
        var root = Bencode.DecodeDictionary(Raw("d4:infod6:lengthi1024e4:name4:fileee"));
        var info = root.Dictionary("info")!;

        Assert.Equal(1024, info.Number("length"));
        Assert.Equal("file", info.Text("name"));
    }

    [Fact]
    public void A_value_remembers_where_it_sat()
    {
        // This is what makes an info hash possible: the info dictionary is
        // hashed as written, not as this would re-encode it.
        byte[] data = Raw("d4:infod6:lengthi1024eee");
        var info = Bencode.DecodeDictionary(data).Dictionary("info")!;

        Assert.Equal("d6:lengthi1024ee", Encoding.ASCII.GetString(data, info.Start, info.Length));
    }

    [Fact]
    public void The_recorded_span_survives_a_key_order_a_re_encoding_would_change()
    {
        // Some torrents in the wild are not canonically ordered. Re-encoding
        // would sort them and change the hash; the span does not.
        byte[] data = Raw("d4:infod4:name4:file6:lengthi1e4:aardi2eee");
        var info = Bencode.DecodeDictionary(data).Dictionary("info")!;

        string raw = Encoding.ASCII.GetString(data, info.Start, info.Length);

        Assert.StartsWith("d4:name", raw);
        Assert.NotEqual(raw, Encoding.ASCII.GetString(Bencode.Encode(info)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("l")]
    [InlineData("i42")]
    [InlineData("5:abc")]
    [InlineData("x")]
    [InlineData("d3:key")]
    [InlineData("di42ei1ee")]        // a dictionary key that is not a string
    [InlineData("-1:a")]
    public void Malformed_input_is_refused_with_a_position(string text)
    {
        var error = Assert.Throws<BencodeException>(() => Bencode.Decode(Raw(text)));
        Assert.True(error.Position >= 0);
    }

    [Fact]
    public void Nesting_far_enough_to_be_hostile_is_refused()
    {
        byte[] data = Raw(new string('l', 200) + new string('e', 200));

        Assert.Throws<BencodeException>(() => Bencode.Decode(data));
    }

    [Fact]
    public void Encoding_round_trips_a_canonical_document()
    {
        byte[] data = Raw("d1:ai1e1:bl4:spamee");

        Assert.Equal(data, Bencode.Encode(Bencode.Decode(data)));
    }

    [Fact]
    public void Encoded_dictionary_keys_come_out_sorted()
    {
        var dictionary = Bencode.Dictionary(
            ("zed", Bencode.Number(1)),
            ("alpha", Bencode.Number(2)));

        Assert.Equal("d5:alphai2e3:zedi1ee", Encoding.ASCII.GetString(Bencode.Encode(dictionary)));
    }

    [Fact]
    public void A_top_level_value_that_is_not_a_dictionary_is_refused() =>
        Assert.Throws<BencodeException>(() => Bencode.DecodeDictionary(Raw("i42e")));

    private static byte[] Raw(string text) => Encoding.ASCII.GetBytes(text);
}
