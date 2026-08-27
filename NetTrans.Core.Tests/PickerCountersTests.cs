using NetTrans.Models;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The counters that replaced four full scans of the piece table.
///
/// `IsComplete` was asked once per message a peer sent and answered by walking
/// every piece under the lock, so these check the cheap answers against the
/// state they summarise rather than against themselves.
/// </summary>
public class PickerCountersTests
{
    [Fact]
    public void A_fresh_picker_wants_everything()
    {
        var picker = new PiecePicker(8);

        Assert.Equal(0, picker.CompletedCount);
        Assert.Equal(8, picker.RemainingCount);
        Assert.False(picker.IsComplete);
    }

    [Fact]
    public void Completing_pieces_moves_both_counts()
    {
        var picker = new PiecePicker(4);

        picker.Complete(0);
        picker.Complete(2);

        Assert.Equal(2, picker.CompletedCount);
        Assert.Equal(2, picker.RemainingCount);

        // The same piece twice is one piece; a peer that sends a duplicate must
        // not move the totals.
        picker.Complete(2);

        Assert.Equal(2, picker.CompletedCount);
        Assert.Equal(2, picker.RemainingCount);
    }

    [Fact]
    public void Everything_completed_is_complete()
    {
        var picker = new PiecePicker(3);

        for (int i = 0; i < 3; i++) picker.Complete(i);

        Assert.True(picker.IsComplete);
        Assert.Equal(0, picker.RemainingCount);
        Assert.Equal(3, picker.CompletedCount);
    }

    [Fact]
    public void A_deselected_piece_is_not_counted_as_remaining()
    {
        var picker = new PiecePicker(4);
        picker.WantOnly(new[] { 0, 1 });

        Assert.Equal(2, picker.RemainingCount);

        picker.Complete(0);
        picker.Complete(1);

        // 2 and 3 were never wanted, so nothing is left even though half the
        // torrent is missing.
        Assert.True(picker.IsComplete);
        Assert.Equal(2, picker.CompletedCount);
    }

    [Fact]
    public void Narrowing_after_some_pieces_landed_counts_what_is_left()
    {
        var picker = new PiecePicker(6);
        picker.Complete(0);
        picker.Complete(4);

        picker.WantOnly(new[] { 0, 1, 2, 4 });

        // Of the four wanted, two are already had.
        Assert.Equal(2, picker.RemainingCount);
        Assert.Equal(2, picker.CompletedCount);
    }

    [Fact]
    public void What_a_previous_run_finished_counts_once()
    {
        var picker = new PiecePicker(8);
        var bitfield = new byte[PeerWire.BitfieldLength(8)];

        PeerWire.SetPiece(bitfield, 1);
        PeerWire.SetPiece(bitfield, 5);

        picker.Restore(bitfield);
        picker.Restore(bitfield);

        Assert.Equal(2, picker.CompletedCount);
        Assert.Equal(6, picker.RemainingCount);
    }

    [Fact]
    public void The_endgame_starts_only_once_nothing_is_free()
    {
        var picker = new PiecePicker(3) { Endgame = true };
        var everything = Bitfield(3);

        Assert.False(picker.InEndgame);

        int first = picker.Take(everything);
        int second = picker.Take(everything);
        int third = picker.Take(everything);

        Assert.All(new[] { first, second, third }, taken => Assert.True(taken >= 0));

        // Everything left is assigned to somebody, which is the condition.
        Assert.True(picker.InEndgame);

        picker.Return(second);
        Assert.False(picker.InEndgame);

        picker.Complete(second);
        Assert.True(picker.InEndgame);
    }

    [Fact]
    public void A_piece_completed_while_assigned_does_not_double_count()
    {
        var picker = new PiecePicker(4) { Endgame = true };
        var everything = Bitfield(4);

        int taken = picker.Take(everything);
        picker.Complete(taken);

        Assert.Equal(1, picker.CompletedCount);
        Assert.Equal(3, picker.RemainingCount);

        // Returning a piece that has already landed must not put it back into
        // the free set, or the endgame would never start.
        picker.Return(taken);

        Assert.Equal(3, picker.RemainingCount);
    }

    [Fact]
    public void The_counters_agree_with_a_full_walk_after_a_long_random_run()
    {
        // The point of a counter is that it says the same thing the scan did.
        const int pieces = 300;

        var picker = new PiecePicker(pieces) { Endgame = true };
        var everything = Bitfield(pieces);
        var random = new Random(20260826);
        var taken = new List<int>();

        for (int step = 0; step < 4000; step++)
        {
            switch (random.Next(3))
            {
                case 0:
                    int next = picker.Take(everything);
                    if (next >= 0) taken.Add(next);
                    break;

                case 1 when taken.Count > 0:
                    int back = taken[random.Next(taken.Count)];
                    taken.Remove(back);
                    picker.Return(back);
                    break;

                case 2 when taken.Count > 0:
                    int done = taken[random.Next(taken.Count)];
                    taken.Remove(done);
                    picker.Complete(done);
                    break;
            }
        }

        int walkedComplete = 0;
        int walkedRemaining = 0;

        for (int i = 0; i < pieces; i++)
        {
            if (picker.IsDone(i)) walkedComplete++;
            else if (picker.IsWanted(i)) walkedRemaining++;
        }

        Assert.Equal(walkedComplete, picker.CompletedCount);
        Assert.Equal(walkedRemaining, picker.RemainingCount);
        Assert.Equal(walkedRemaining == 0, picker.IsComplete);
    }

    [Fact]
    public void A_block_arriving_twice_does_not_finish_a_piece_with_a_hole_in_it()
    {
        var buffer = new PieceBuffer(0, PeerWire.BlockLength * 3);
        var block = new byte[PeerWire.BlockLength];

        Assert.True(buffer.Add(0, block));
        Assert.True(buffer.Add(0, block));
        Assert.True(buffer.Add(PeerWire.BlockLength, block));

        // Three adds, two distinct blocks, one still missing.
        Assert.False(buffer.IsComplete);

        Assert.True(buffer.Add(PeerWire.BlockLength * 2, block));
        Assert.True(buffer.IsComplete);
    }

    [Fact]
    public void The_log_keeps_the_recent_lines_and_says_how_many_it_dropped()
    {
        var log = new TransferLog();

        for (int i = 0; i < TransferLog.Keep * 3; i++)
        {
            log.Add(new LogEntry("00:00", $"第 {i} 行"));
        }

        Assert.True(log.Count <= TransferLog.Keep + 100, $"count: {log.Count}");
        Assert.True(log.Dropped > 0);

        // The newest line survives; that is what the tab is open for.
        Assert.Contains(log, entry => entry.Message == $"第 {TransferLog.Keep * 3 - 1} 行");
        Assert.DoesNotContain(log, entry => entry.Message == "第 0 行");
    }

    [Fact]
    public async Task The_log_can_be_read_while_a_transfer_writes_to_it()
    {
        // The plain list this replaced threw here, which is the kind of crash
        // that only shows up on a busy machine.
        var log = new TransferLog();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20_000 && !stopping.IsCancellationRequested; i++)
            {
                log.Add(new LogEntry("00:00", $"第 {i} 行"));
            }
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted && !stopping.IsCancellationRequested)
            {
                foreach (var entry in log) _ = entry.Message.Length;
            }
        });

        await Task.WhenAll(writer, reader);
    }

    private static byte[] Bitfield(int pieces)
    {
        var bits = new byte[PeerWire.BitfieldLength(pieces)];

        for (int i = 0; i < pieces; i++) PeerWire.SetPiece(bits, i);

        return bits;
    }
}
