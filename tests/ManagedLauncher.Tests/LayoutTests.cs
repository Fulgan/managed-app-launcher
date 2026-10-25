using ManagedLauncher.Core.Paging;

namespace ManagedLauncher.Tests;

public class LayoutTests
{
    [Fact]
    public void AppsAndManageFitWithoutNavigation()
    {
        var layout = new LauncherLayout(15, 14);

        Assert.Equal(1, layout.PageCount);
        Assert.False(layout.HasNextPage);
        Assert.Equal(new KeyAssignment(KeyRole.App, 13, 0, 1), layout.Assign(13, 0));
        Assert.Equal(new KeyAssignment(KeyRole.Manage, -1, 0, 1), layout.Assign(14, 0));
    }

    [Fact]
    public void SinglePageLeavesBlanksBeforeManageOnLastKey()
    {
        var layout = new LauncherLayout(15, 9);

        Assert.Equal(KeyRole.App, layout.Assign(8, 0).Role);
        Assert.Equal(KeyRole.Empty, layout.Assign(9, 0).Role);
        Assert.Equal(KeyRole.Empty, layout.Assign(13, 0).Role);
        Assert.Equal(KeyRole.Manage, layout.Assign(14, 0).Role);
    }

    [Fact]
    public void EmptyListShowsOnlyManage()
    {
        var layout = new LauncherLayout(15, 0);

        Assert.Equal(KeyRole.Empty, layout.Assign(0, 0).Role);
        Assert.Equal(KeyRole.Manage, layout.Assign(14, 0).Role);
    }

    [Fact]
    public void ManageCountsTowardOverflow()
    {
        var layout = new LauncherLayout(15, 15);

        Assert.True(layout.HasNextPage);
        Assert.Equal(2, layout.PageCount);
        Assert.Equal(new KeyAssignment(KeyRole.NextPage, -1, 0, 2), layout.Assign(14, 0));
        Assert.Equal(new KeyAssignment(KeyRole.App, 13, 0, 2), layout.Assign(13, 0));
    }

    [Fact]
    public void ManageSitsLeftOfNextPageOnLastPageOnly()
    {
        var layout = new LauncherLayout(15, 16);

        Assert.Equal(new KeyAssignment(KeyRole.App, 14, 1, 2), layout.Assign(0, 1));
        Assert.Equal(new KeyAssignment(KeyRole.App, 15, 1, 2), layout.Assign(1, 1));
        Assert.Equal(KeyRole.Empty, layout.Assign(2, 1).Role);
        Assert.Equal(new KeyAssignment(KeyRole.Manage, -1, 1, 2), layout.Assign(13, 1));
        Assert.Equal(KeyRole.NextPage, layout.Assign(14, 1).Role);
        Assert.Equal(KeyRole.App, layout.Assign(13, 0).Role);
    }

    [Fact]
    public void FullLastPagePushesManageToNewPage()
    {
        var layout = new LauncherLayout(15, 28);

        Assert.Equal(3, layout.PageCount);
        Assert.Equal(new KeyAssignment(KeyRole.App, 27, 1, 3), layout.Assign(13, 1));
        Assert.Equal(KeyRole.Empty, layout.Assign(0, 2).Role);
        Assert.Equal(KeyRole.Manage, layout.Assign(13, 2).Role);
    }

    [Theory]
    [InlineData(14, 1)]
    [InlineData(15, 2)]
    [InlineData(27, 2)]
    [InlineData(28, 3)]
    [InlineData(41, 3)]
    public void PageCountIncludesManage(int apps, int pages)
    {
        Assert.Equal(pages, new LauncherLayout(15, apps).PageCount);
    }

    [Fact]
    public void StepWrapsBothWays()
    {
        var layout = new LauncherLayout(15, 41);

        Assert.Equal(0, layout.Step(2, +1));
        Assert.Equal(2, layout.Step(0, -1));
    }

    [Fact]
    public void PageIsClampedWhenListShrinks()
    {
        var layout = new LauncherLayout(15, 10);

        Assert.Equal(0, layout.Clamp(3));
        Assert.Equal(KeyRole.App, layout.Assign(0, 3).Role);
    }

    [Fact]
    public void MiniUsesSixKeys()
    {
        var layout = new LauncherLayout(6, 6);

        Assert.Equal(2, layout.PageCount);
        Assert.Equal(new KeyAssignment(KeyRole.App, 5, 1, 2), layout.Assign(0, 1));
        Assert.Equal(KeyRole.Manage, layout.Assign(4, 1).Role);
    }

    [Fact]
    public void BackTakesFirstKeyOfFirstPageOnly()
    {
        var layout = new LauncherLayout(15, 13, HasBack: true);

        Assert.Equal(1, layout.PageCount);
        Assert.Equal(KeyRole.Back, layout.Assign(0, 0).Role);
        Assert.Equal(new KeyAssignment(KeyRole.App, 0, 0, 1), layout.Assign(1, 0));
        Assert.Equal(new KeyAssignment(KeyRole.App, 12, 0, 1), layout.Assign(13, 0));
        Assert.Equal(KeyRole.Manage, layout.Assign(14, 0).Role);
    }

    [Fact]
    public void BackOnlyOnFirstPageWhenPaged()
    {
        var layout = new LauncherLayout(15, 14, HasBack: true);

        Assert.True(layout.HasNextPage);
        Assert.Equal(14, layout.Capacity);
        Assert.Equal(2, layout.PageCount);
        Assert.Equal(KeyRole.Back, layout.Assign(0, 0).Role);
        Assert.Equal(new KeyAssignment(KeyRole.App, 12, 0, 2), layout.Assign(13, 0));
        Assert.Equal(KeyRole.NextPage, layout.Assign(14, 0).Role);
        Assert.Equal(new KeyAssignment(KeyRole.App, 13, 1, 2), layout.Assign(0, 1));
        Assert.Equal(KeyRole.Manage, layout.Assign(13, 1).Role);
        Assert.Equal(KeyRole.Empty, layout.Assign(5, 1).Role);
    }

    [Fact]
    public void BackPushesManageOntoSecondPageWhenFull()
    {
        Assert.Equal(1, new LauncherLayout(15, 14).PageCount);
        Assert.Equal(2, new LauncherLayout(15, 14, HasBack: true).PageCount);
    }
}
