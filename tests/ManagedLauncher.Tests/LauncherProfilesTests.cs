using ManagedLauncher.Core.Paging;

namespace ManagedLauncher.Tests;

public class LauncherProfilesTests
{
    [Fact]
    public void TableIsConsistent()
    {
        var all = LauncherProfiles.All;

        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(p => p.DeviceType).Distinct().Count());
        Assert.Equal(all.Count, all.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, p => Assert.True(p.Columns > 0 && p.Rows > 0 && p.Model.Length > 0 && p.Label.Length > 0, p.Name));
    }

    [Fact]
    public void ProfileIsChosenByTypeThenByLayout()
    {
        Assert.Equal("profiles/Managed App Launcher Neo", LauncherProfiles.For(9, 4, 2)?.ManifestName);
        Assert.Equal("profiles/Managed App Launcher XL", LauncherProfiles.For(null, 8, 4)?.ManifestName);
        Assert.Null(LauncherProfiles.For(null, 7, 7));
    }

    [Fact]
    public void ManifestEntriesNeverAutoSwitch()
    {
        var entries = LauncherProfiles.ManifestEntries();

        Assert.Equal(LauncherProfiles.All.Count, entries.Count);
        Assert.All(entries, e => Assert.True(e!["DontAutoSwitchWhenInstalled"]!.GetValue<bool>()));
    }
}
