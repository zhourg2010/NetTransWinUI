using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>按分类建子文件夹, and the suffix that stops one download eating another.</summary>
public class SavePathPlannerTests
{
    [Theory]
    [InlineData("soft", "软件")]
    [InlineData("video", "视频")]
    [InlineData("doc", "文档")]
    [InlineData("music", "音乐")]
    [InlineData("bt", "BT")]
    public void Each_category_gets_its_own_folder(string category, string folder) =>
        Assert.Equal(Path.Combine(@"D:\Downloads", folder), SavePathPlanner.Directory(@"D:\Downloads", category, byCategory: true));

    [Fact]
    public void The_switch_off_means_the_root_unchanged() =>
        Assert.Equal(@"D:\Downloads", SavePathPlanner.Directory(@"D:\Downloads", "soft", byCategory: false));

    [Theory]
    [InlineData("all")]     // the 全部 tab, not something a file belongs to
    [InlineData("")]
    [InlineData(null)]
    [InlineData("未知分类")]
    public void A_category_with_no_folder_of_its_own_stays_in_the_root(string? category) =>
        Assert.Equal(@"D:\Downloads", SavePathPlanner.Directory(@"D:\Downloads", category, byCategory: true));

    [Fact]
    public void A_free_name_is_used_as_it_is() =>
        Assert.Equal("setup.exe", SavePathPlanner.UniqueName(@"D:\x", "setup.exe", _ => false));

    [Fact]
    public void A_taken_name_gets_the_next_number()
    {
        var taken = new HashSet<string> { @"D:\x\setup.exe", @"D:\x\setup (2).exe" };

        Assert.Equal("setup (3).exe", SavePathPlanner.UniqueName(@"D:\x", "setup.exe", taken.Contains));
    }

    [Fact]
    public void The_suffix_goes_before_the_extension_not_after()
    {
        var taken = new HashSet<string> { @"D:\x\archive.tar.gz" };

        // Only the last extension is one, which is what Path itself thinks and
        // what the shell would do.
        Assert.Equal("archive.tar (2).gz", SavePathPlanner.UniqueName(@"D:\x", "archive.tar.gz", taken.Contains));
    }

    [Fact]
    public void A_name_with_no_extension_still_numbers_cleanly()
    {
        var taken = new HashSet<string> { @"D:\x\README" };

        Assert.Equal("README (2)", SavePathPlanner.UniqueName(@"D:\x", "README", taken.Contains));
    }

    [Fact]
    public void A_directory_that_claims_everything_is_taken_gives_up_rather_than_hanging() =>
        Assert.Equal("setup.exe", SavePathPlanner.UniqueName(@"D:\x", "setup.exe", _ => true));
}
