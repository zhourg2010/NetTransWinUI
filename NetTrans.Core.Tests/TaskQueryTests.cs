using NetTrans.Models;
using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The list pipeline, against the orders App2 produces for the same seed.</summary>
public class TaskQueryTests
{
    private static readonly List<DownloadItem> Seed = Golden.Seed();

    public static TheoryData<string, string> SortCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var sort in Golden.Data.Sorting) data.Add(sort.SortKey, sort.SortDirection);
        return data;
    }

    public static TheoryData<string, string> FilterCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var filter in Golden.Data.Filtering) data.Add(filter.Tab, filter.Category);
        return data;
    }

    public static TheoryData<string> SearchCases()
    {
        var data = new TheoryData<string>();
        foreach (var search in Golden.Data.Searching) data.Add(search.Query);
        return data;
    }

    public static TheoryData<int, bool> FoldCases()
    {
        var data = new TheoryData<int, bool>();
        foreach (var fold in Golden.Data.Folding) data.Add(fold.Total, fold.Expanded);
        return data;
    }

    [Theory]
    [MemberData(nameof(SortCases))]
    public void Sorting_matches_the_prototype(string sortKey, string sortDirection)
    {
        var expected = Golden.Data.Sorting.Single(s => s.SortKey == sortKey && s.SortDirection == sortDirection);
        var actual = TaskQuery.Apply(Seed, item => item, "all", "all", "", sortKey, sortDirection);

        Assert.Equal(expected.Ids, actual.Select(t => t.Id));
    }

    /// <summary>
    /// The prototype negates its comparator rather than reversing the list, and
    /// Array.prototype.sort is stable, so rows that tie keep seed order in both
    /// directions. Five tasks share a speed of zero, which makes this visible.
    /// </summary>
    [Fact]
    public void Descending_keeps_tied_rows_in_seed_order()
    {
        var expected = Golden.Data.Sorting.Single(s => s is { SortKey: "speed", SortDirection: "desc" });
        var actual = TaskQuery.Apply(Seed, item => item, "all", "all", "", "speed", "desc");

        Assert.Equal(expected.Ids, actual.Select(t => t.Id));

        var stalled = actual.Where(t => t.Speed == 0).Select(t => t.Id).ToList();
        Assert.Equal(Seed.Where(t => t.Speed == 0).Select(t => t.Id), stalled);
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Tab_and_category_filtering_match_the_prototype(string tab, string category)
    {
        var expected = Golden.Data.Filtering.Single(f => f.Tab == tab && f.Category == category);
        var actual = TaskQuery.Apply(Seed, item => item, tab, category, "", "added", "asc");

        Assert.Equal(expected.Ids, actual.Select(t => t.Id));
        Assert.Equal(expected.ActiveCount, TaskQuery.ActiveCount(Seed, item => item, category));
        Assert.Equal(expected.DoneCount, TaskQuery.DoneCount(Seed, item => item, category));
    }

    [Theory]
    [MemberData(nameof(SearchCases))]
    public void Search_matches_the_prototype(string query)
    {
        var expected = Golden.Data.Searching.Single(s => s.Query == query);
        var actual = TaskQuery.Apply(Seed, item => item, "all", "all", query, "added", "asc");

        Assert.Equal(expected.Ids, actual.Select(t => t.Id));
    }

    [Fact]
    public void Search_ignores_case()
    {
        var lower = TaskQuery.Apply(Seed, item => item, "all", "all", "ubuntu", "added", "asc");
        var upper = TaskQuery.Apply(Seed, item => item, "all", "all", "UBUNTU", "added", "asc");

        Assert.Equal(lower.Select(t => t.Id), upper.Select(t => t.Id));
        Assert.Single(lower);
    }

    [Theory]
    [MemberData(nameof(FoldCases))]
    public void Folding_matches_the_prototype(int total, bool expanded)
    {
        var expected = Golden.Data.Folding.Single(f => f.Total == total && f.Expanded == expanded);
        var actual = TaskQuery.Fold(total, expanded);

        Assert.Equal(expected.Shown, actual.Shown);
        Assert.Equal(expected.Hidden, actual.Hidden);
        Assert.Equal(expected.CanFold, actual.CanFold);
    }

    [Fact]
    public void Folding_shows_everything_once_expanded()
    {
        var fold = TaskQuery.Fold(total: 12, expanded: true);
        Assert.Equal(12, fold.Shown);
        Assert.Equal(0, fold.Hidden);
        Assert.True(fold.CanFold);
    }

    /// <summary>
    /// 加入时间 sorts by id in the handoff, so reordering the queue does not
    /// reshuffle the list. Pinned here because it looks like a bug otherwise.
    /// </summary>
    [Fact]
    public void Added_sorts_by_id_not_by_queue_position()
    {
        var moved = Seed.ToList();
        var last = moved[^1];
        moved.RemoveAt(moved.Count - 1);
        moved.Insert(0, last);

        var actual = TaskQuery.Apply(moved, item => item, "all", "all", "", "added", "asc");
        Assert.Equal(Seed.Select(t => t.Id).OrderBy(id => id), actual.Select(t => t.Id));
    }

    [Fact]
    public void An_empty_list_folds_to_nothing()
    {
        var fold = TaskQuery.Fold(total: 0, expanded: false);
        Assert.Equal(0, fold.Shown);
        Assert.Equal(0, fold.Hidden);
        Assert.False(fold.CanFold);
    }

    [Fact]
    public void The_projection_lets_the_shell_query_wrappers()
    {
        var wrapped = Seed.Select(item => new { Model = item }).ToList();
        var actual = TaskQuery.Apply(wrapped, w => w.Model, "active", "all", "", "added", "asc");

        Assert.Equal(
            Golden.Data.Filtering.Single(f => f is { Tab: "active", Category: "all" }).Ids,
            actual.Select(w => w.Model.Id));
    }
}
