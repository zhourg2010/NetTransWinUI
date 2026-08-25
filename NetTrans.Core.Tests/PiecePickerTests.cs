using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Which piece to ask for next. The rule that matters is rarest-first: everyone
/// grabbing piece 0 leaves the rare pieces rarer, and a torrent whose last
/// piece has one seed stalls for the whole swarm.
/// </summary>
public class PiecePickerTests
{
    [Fact]
    public void A_piece_is_only_handed_out_once()
    {
        var picker = new PiecePicker(4);
        var everything = Bitfield(4, 0, 1, 2, 3);

        var taken = new[] { picker.Take(everything), picker.Take(everything), picker.Take(everything), picker.Take(everything) };

        Assert.Equal(4, taken.Distinct().Count());
        Assert.Equal(-1, picker.Take(everything));
    }

    [Fact]
    public void The_rarest_wanted_piece_goes_first()
    {
        var picker = new PiecePicker(4);

        // Three peers have pieces 0-2; only one has piece 3.
        picker.Saw(Bitfield(4, 0, 1, 2));
        picker.Saw(Bitfield(4, 0, 1, 2));
        picker.Saw(Bitfield(4, 0, 1, 2, 3));

        Assert.Equal(3, picker.Take(Bitfield(4, 0, 1, 2, 3)));
    }

    [Fact]
    public void Equally_rare_pieces_are_taken_in_order()
    {
        // So a partly-done file is contiguous where the swarm allows it.
        var picker = new PiecePicker(4);
        picker.Saw(Bitfield(4, 0, 1, 2, 3));

        Assert.Equal(0, picker.Take(Bitfield(4, 0, 1, 2, 3)));
        Assert.Equal(1, picker.Take(Bitfield(4, 0, 1, 2, 3)));
    }

    [Fact]
    public void A_peer_is_only_offered_what_it_has()
    {
        var picker = new PiecePicker(4);
        picker.Saw(Bitfield(4, 0, 1, 2, 3));

        Assert.Equal(2, picker.Take(Bitfield(4, 2)));
        Assert.Equal(-1, picker.Take(Bitfield(4, 2)));
    }

    [Fact]
    public void A_returned_piece_is_offered_again()
    {
        var picker = new PiecePicker(2);
        var all = Bitfield(2, 0, 1);

        int taken = picker.Take(all);
        picker.Return(taken);

        Assert.Equal(taken, picker.Take(all));
    }

    [Fact]
    public void A_completed_piece_is_never_offered_again()
    {
        var picker = new PiecePicker(2);
        var all = Bitfield(2, 0, 1);

        picker.Complete(picker.Take(all));
        picker.Complete(picker.Take(all));

        Assert.Equal(-1, picker.Take(all));
        Assert.True(picker.IsComplete);
        Assert.Equal(2, picker.CompletedCount);
    }

    [Fact]
    public void Progress_is_reported_as_it_goes()
    {
        var picker = new PiecePicker(4);
        Assert.Equal(0, picker.CompletedCount);
        Assert.False(picker.IsComplete);

        picker.Complete(2);

        Assert.Equal(1, picker.CompletedCount);
        Assert.True(picker.IsDone(2));
        Assert.False(picker.IsDone(0));
    }

    [Fact]
    public void What_a_previous_run_finished_is_restored()
    {
        var picker = new PiecePicker(8);
        picker.Restore(Bitfield(8, 0, 3, 7));

        Assert.Equal(3, picker.CompletedCount);
        Assert.True(picker.IsDone(3));

        // And is not asked for again.
        var all = Bitfield(8, 0, 1, 2, 3, 4, 5, 6, 7);
        var offered = new List<int>();

        for (int taken = picker.Take(all); taken >= 0; taken = picker.Take(all)) offered.Add(taken);

        Assert.Equal(new[] { 1, 2, 4, 5, 6 }, offered);
    }

    [Fact]
    public void The_bitfield_it_publishes_matches_what_it_has_finished()
    {
        var picker = new PiecePicker(10);
        picker.Complete(0);
        picker.Complete(9);

        var bits = picker.Bitfield();

        Assert.Equal(PeerWire.BitfieldLength(10), bits.Length);
        Assert.True(PeerWire.HasPiece(bits, 0));
        Assert.True(PeerWire.HasPiece(bits, 9));
        Assert.False(PeerWire.HasPiece(bits, 5));
    }

    [Fact]
    public void Interest_is_whether_the_peer_has_anything_still_wanted()
    {
        var picker = new PiecePicker(3);

        Assert.True(picker.WantsAnythingFrom(Bitfield(3, 1)));

        picker.Complete(1);
        Assert.False(picker.WantsAnythingFrom(Bitfield(3, 1)));
        Assert.True(picker.WantsAnythingFrom(Bitfield(3, 0, 1)));
    }

    [Fact]
    public void A_peer_leaving_takes_its_pieces_out_of_the_rarity_count()
    {
        var picker = new PiecePicker(2);
        var all = Bitfield(2, 0, 1);

        // Three peers have both pieces, and a fourth announced piece 1, so
        // piece 0 is the rarer of the two.
        picker.Saw(all);
        picker.Saw(all);
        picker.Saw(all);
        picker.Saw(1);

        Assert.Equal(0, picker.Take(all));
        picker.Return(0);

        // Two of those peers disconnect, taking their piece 1 with them, which
        // makes piece 1 the rarer one now.
        picker.Left(Bitfield(2, 1));
        picker.Left(Bitfield(2, 1));

        Assert.Equal(1, picker.Take(all));
    }

    [Fact]
    public void A_have_message_counts_towards_rarity()
    {
        var picker = new PiecePicker(2);
        picker.Saw(Bitfield(2, 0, 1));
        picker.Saw(1);

        // Piece 0 is now the rarer of the two.
        Assert.Equal(0, picker.Take(Bitfield(2, 0, 1)));
    }

    [Fact]
    public void Two_threads_never_get_the_same_piece()
    {
        var picker = new PiecePicker(500);
        var all = Bitfield(500, Enumerable.Range(0, 500).ToArray());

        var taken = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            for (int piece = picker.Take(all); piece >= 0; piece = picker.Take(all)) taken.Add(piece);
        });

        Assert.Equal(500, taken.Count);
        Assert.Equal(500, taken.Distinct().Count());
    }

    private static byte[] Bitfield(int pieces, params int[] have)
    {
        var bits = new byte[PeerWire.BitfieldLength(pieces)];
        foreach (int piece in have) PeerWire.SetPiece(bits, piece);

        return bits;
    }
}
