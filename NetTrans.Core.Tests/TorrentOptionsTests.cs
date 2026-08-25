using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The qBittorrent behaviours worth borrowing: when to stop seeding, which
/// files to fetch, and rebuilding what we have by hashing the disk.
/// </summary>
public class TorrentOptionsTests
{
    // ── seeding limits ────────────────────────────────────────────────────

    [Fact]
    public void A_ratio_limit_stops_at_the_ratio()
    {
        var limits = SeedingLimits.Ratio(2.0);

        Assert.False(limits.Reached(uploaded: 100, downloaded: 100, TimeSpan.Zero));
        Assert.False(limits.Reached(uploaded: 199, downloaded: 100, TimeSpan.Zero));
        Assert.True(limits.Reached(uploaded: 200, downloaded: 100, TimeSpan.Zero));
    }

    [Fact]
    public void A_time_limit_stops_after_the_time()
    {
        var limits = new SeedingLimits(MaxSeedingTime: TimeSpan.FromHours(2));

        Assert.False(limits.Reached(0, 100, TimeSpan.FromHours(1.9)));
        Assert.True(limits.Reached(0, 100, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Either_limit_being_met_is_enough()
    {
        var limits = new SeedingLimits(MaxRatio: 5, MaxSeedingTime: TimeSpan.FromHours(1));

        Assert.True(limits.Reached(uploaded: 500, downloaded: 100, TimeSpan.Zero));
        Assert.True(limits.Reached(uploaded: 0, downloaded: 100, TimeSpan.FromHours(1)));
        Assert.False(limits.Reached(uploaded: 100, downloaded: 100, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void No_limit_never_stops()
    {
        Assert.True(SeedingLimits.Forever.IsUnlimited);
        Assert.False(SeedingLimits.Forever.Reached(long.MaxValue, 1, TimeSpan.FromDays(365)));
    }

    [Fact]
    public void A_torrent_seeded_from_files_already_on_disk_has_no_denominator()
    {
        // Nothing was downloaded, so the ratio is not 500/0 -- it is unbounded,
        // and a ratio limit is met the moment anything is uploaded.
        Assert.Equal(double.PositiveInfinity, SeedingLimits.RatioOf(uploaded: 500, downloaded: 0));
        Assert.Equal(0, SeedingLimits.RatioOf(uploaded: 0, downloaded: 0));
        Assert.True(SeedingLimits.Ratio(2).Reached(uploaded: 1, downloaded: 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_limit_reads_back_as_something_a_person_can_check()
    {
        Assert.Equal("一直做种", SeedingLimits.Forever.Describe());
        Assert.Equal("分享率 2", SeedingLimits.Ratio(2).Describe());
        Assert.Equal("90 分钟", new SeedingLimits(MaxSeedingTime: TimeSpan.FromMinutes(90)).Describe());
        Assert.Equal("分享率 1.5 或 60 分钟", new SeedingLimits(1.5, TimeSpan.FromHours(1)).Describe());
    }

    // ── file selection ────────────────────────────────────────────────────

    [Theory]
    [InlineData("release/movie.bin")]
    [InlineData("movie.bin")]
    [InlineData("release\\movie.bin")]
    public void A_chosen_name_is_matched_however_the_caller_kept_it(string chosen)
    {
        // The paths in a multi-file torrent start with the torrent's own name
        // and use whichever separator the platform builds; a caller that kept
        // only what it displayed still means this file.
        var torrent = TwoFiles();

        var picked = FileSelection.Choose(torrent, new[] { chosen });

        Assert.Equal("movie.bin", Path.GetFileName(Assert.Single(picked).Path));
    }

    [Fact]
    public void A_name_that_is_in_no_torrent_matches_nothing() =>
        Assert.Empty(FileSelection.Choose(TwoFiles(), new[] { "somethingelse.bin" }));

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(1.0, null, false)]
    [InlineData(null, 60.0, false)]
    [InlineData(null, 0.0, true)]
    public void 下完即停_is_told_apart_from_a_limit(double? ratio, double? minutes, bool immediate)
    {
        var limits = new SeedingLimits(ratio, minutes is { } m ? TimeSpan.FromMinutes(m) : null);

        Assert.Equal(immediate, limits.StopsImmediately);
    }

    /// <summary>A two-file torrent whose paths carry the torrent's name.</summary>
    private static TorrentMetainfo TwoFiles()
    {
        var builder = new TorrentBuilder { Name = "release", PieceLength = 256 };

        builder.Add("movie.bin", new byte[700]);
        builder.Add("sample.bin", new byte[300]);

        return TorrentMetainfo.Parse(builder.Build());
    }

    [Fact]
    public void A_files_pieces_are_the_ones_its_bytes_fall_in()
    {
        // 256-byte pieces: a.bin is 0..99 (piece 0), b.bin is 100..399 (0-1).
        var torrent = Torrent();

        Assert.Equal(new[] { 0 }, FileSelection.PiecesOf(torrent, torrent.Files[0]));
        Assert.Equal(new[] { 0, 1 }, FileSelection.PiecesOf(torrent, torrent.Files[1]));
    }

    [Fact]
    public void Selecting_one_file_wants_only_its_pieces()
    {
        var torrent = Torrent();

        Assert.Equal(new[] { 0 }, FileSelection.WantedPieces(torrent, new[] { torrent.Files[0] }));
        Assert.Equal(new[] { 0, 1 }, FileSelection.WantedPieces(torrent, new[] { torrent.Files[1] }));
    }

    [Fact]
    public void A_piece_straddling_a_wanted_and_an_unwanted_file_is_still_wanted()
    {
        // Piece 0 holds all of a.bin and the front of b.bin. Wanting b.bin means
        // wanting piece 0, because a piece cannot be had in halves -- which is
        // also why deselecting a small neighbour often saves nothing.
        var torrent = Torrent();

        Assert.Contains(0, FileSelection.WantedPieces(torrent, new[] { torrent.Files[1] }));
    }

    [Fact]
    public void The_cost_of_a_selection_counts_a_straddling_piece_once()
    {
        var torrent = Torrent();

        // b.bin is 300 bytes but needs both pieces: 256 + 144.
        Assert.Equal(400, FileSelection.BytesFor(torrent, new[] { torrent.Files[1] }));

        // Adding a.bin costs nothing more; its piece was already needed.
        Assert.Equal(400, FileSelection.BytesFor(torrent, torrent.Files));
    }

    [Fact]
    public void Selecting_nothing_wants_nothing() =>
        Assert.Empty(FileSelection.WantedPieces(Torrent(), Array.Empty<TorrentEntry>()));

    [Fact]
    public void A_picker_narrowed_to_a_selection_skips_the_rest()
    {
        var picker = new PiecePicker(8);
        picker.WantOnly(new[] { 2, 3 });

        var all = Bitfield(8, 0, 1, 2, 3, 4, 5, 6, 7);
        var offered = new List<int>();

        for (int piece = picker.Take(all); piece >= 0; piece = picker.Take(all)) offered.Add(piece);

        Assert.Equal(new[] { 2, 3 }, offered);
        Assert.Equal(2, picker.RemainingCount);
    }

    [Fact]
    public void A_deselected_piece_does_not_hold_completion_back()
    {
        var picker = new PiecePicker(4);
        picker.WantOnly(new[] { 0, 1 });

        picker.Complete(0);
        picker.Complete(1);

        // Pieces 2 and 3 were never wanted, so the torrent is done.
        Assert.True(picker.IsComplete);
        Assert.False(picker.IsWanted(2));
    }

    [Fact]
    public void Interest_ignores_pieces_that_were_deselected()
    {
        var picker = new PiecePicker(4);
        picker.WantOnly(new[] { 0 });

        Assert.False(picker.WantsAnythingFrom(Bitfield(4, 2, 3)));
        Assert.True(picker.WantsAnythingFrom(Bitfield(4, 0)));
    }

    // ── sequential and endgame ────────────────────────────────────────────

    [Fact]
    public void Sequential_takes_the_first_piece_it_can_regardless_of_rarity()
    {
        var picker = new PiecePicker(4) { Sequential = true };

        // Piece 3 is rarest, which rarest-first would take first.
        picker.Saw(Bitfield(4, 0, 1, 2));
        picker.Saw(Bitfield(4, 0, 1, 2));
        picker.Saw(Bitfield(4, 0, 1, 2, 3));

        Assert.Equal(0, picker.Take(Bitfield(4, 0, 1, 2, 3)));
        Assert.Equal(1, picker.Take(Bitfield(4, 0, 1, 2, 3)));
    }

    [Fact]
    public void The_endgame_is_off_until_there_is_nothing_unassigned_left()
    {
        var picker = new PiecePicker(4) { Endgame = true };
        var all = Bitfield(4, 0, 1, 2, 3);

        // A threshold on remaining pieces would put a four-piece torrent in the
        // endgame from the first request and duplicate everything.
        Assert.False(picker.InEndgame);
        Assert.Equal(0, picker.Take(all));
        Assert.False(picker.InEndgame);
        Assert.Equal(1, picker.Take(all));
    }

    [Fact]
    public void Once_everything_left_is_assigned_a_piece_may_go_to_a_second_peer()
    {
        var picker = new PiecePicker(2) { Endgame = true };
        var all = Bitfield(2, 0, 1);

        Assert.Equal(0, picker.Take(all));
        Assert.Equal(1, picker.Take(all));

        // Now nothing is free, so the slowest peer is raced rather than waited on.
        Assert.True(picker.InEndgame);
        Assert.Equal(0, picker.Take(all));
    }

    [Fact]
    public void With_the_endgame_off_an_exhausted_picker_still_says_no()
    {
        var picker = new PiecePicker(2);
        var all = Bitfield(2, 0, 1);

        picker.Take(all);
        picker.Take(all);

        Assert.False(picker.InEndgame);
        Assert.Equal(-1, picker.Take(all));
    }

    [Fact]
    public void A_completed_piece_ends_the_endgame_for_that_piece()
    {
        var picker = new PiecePicker(2) { Endgame = true };
        var all = Bitfield(2, 0, 1);

        picker.Take(all);
        picker.Take(all);
        picker.Complete(0);

        // Only piece 1 is left, and it is the only thing on offer.
        Assert.Equal(1, picker.Take(all));
        Assert.Equal(1, picker.Take(all));
    }

    // ── force recheck ─────────────────────────────────────────────────────

    [Fact]
    public async Task Rechecking_finds_the_pieces_the_files_already_satisfy()
    {
        var builder = new TorrentBuilder { Name = "shared.bin", PieceLength = 256 }.Add("shared.bin", 700, 0x5C);
        var torrent = TorrentMetainfo.Parse(builder.Build());
        var sinks = new MemoryFileSinkFactory();

        await using var store = new PieceStore(torrent, sinks, "/downloads");

        // Two of three pieces on disk, as an interrupted run would leave.
        await store.WriteAsync(0, builder.Content()[..256], CancellationToken.None);
        await store.WriteAsync(2, builder.Content()[512..], CancellationToken.None);

        var bits = await TorrentVerifier.VerifyAsync(torrent, store);

        Assert.True(PeerWire.HasPiece(bits, 0));
        Assert.False(PeerWire.HasPiece(bits, 1));
        Assert.True(PeerWire.HasPiece(bits, 2));
    }

    [Fact]
    public async Task Rechecking_a_finished_torrent_finds_all_of_it()
    {
        var builder = new TorrentBuilder { Name = "shared.bin", PieceLength = 256 }.Add("shared.bin", 700, 0x11);
        var torrent = TorrentMetainfo.Parse(builder.Build());

        await using var store = new PieceStore(torrent, new MemoryFileSinkFactory(), "/downloads");

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            int length = (int)torrent.LengthOfPiece(piece);
            await store.WriteAsync(piece, builder.Content().AsSpan(piece * 256, length).ToArray(), CancellationToken.None);
        }

        var bits = await TorrentVerifier.VerifyAsync(torrent, store);

        for (int piece = 0; piece < torrent.PieceCount; piece++) Assert.True(PeerWire.HasPiece(bits, piece));
    }

    [Fact]
    public async Task Rechecking_an_empty_folder_finds_nothing()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 512, 3);
        var torrent = TorrentMetainfo.Parse(builder.Build());

        await using var store = new PieceStore(torrent, new MemoryFileSinkFactory(), "/downloads");

        var bits = await TorrentVerifier.VerifyAsync(torrent, store);

        // A pre-sized file of zeros must not be mistaken for content.
        Assert.All(bits, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Rechecking_reports_progress_because_it_takes_minutes_on_a_real_torrent()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 1024, 8);
        var torrent = TorrentMetainfo.Parse(builder.Build());

        await using var store = new PieceStore(torrent, new MemoryFileSinkFactory(), "/downloads");

        // A synchronous IProgress, not Progress<T>: the latter marshals to a
        // thread pool, which would make this test race its own assertion.
        var seen = new Reported();
        await TorrentVerifier.VerifyAsync(torrent, store, seen);

        Assert.Equal(torrent.PieceCount, seen.Last);
        Assert.Equal(torrent.PieceCount, seen.Count);
    }

    private sealed class Reported : IProgress<int>
    {
        public int Last { get; private set; }

        public int Count { get; private set; }

        public void Report(int value)
        {
            Last = value;
            Count++;
        }
    }

    private static TorrentMetainfo Torrent() => TorrentMetainfo.Parse(
        new TorrentBuilder { Name = "set", PieceLength = 256 }
            .Add("a.bin", 100, 1)
            .Add("b.bin", 300, 2)
            .Build());

    private static byte[] Bitfield(int pieces, params int[] have)
    {
        var bits = new byte[PeerWire.BitfieldLength(pieces)];
        foreach (int piece in have) PeerWire.SetPiece(bits, piece);

        return bits;
    }
}
