using ManagedLauncher.Core;

namespace ManagedLauncher.Tests;

public class AppPathsTests
{
    [Fact]
    public void ConfigFileLivesUnderRoamingAppData()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(roaming, "ManagedAppLauncher", "config.json"), AppPaths.ConfigFile);
    }

    [Fact]
    public void LegacySettingsAreCopiedOnceAndNeverOverwrite()
    {
        using var tmp = new TempDir();
        var from = tmp.MakeDir("old");
        var to = Path.Combine(tmp.Path, "new");
        File.WriteAllText(Path.Combine(from, "config.json"), "{\"old\":1}");
        File.WriteAllText(Path.Combine(from, "devices.json"), "[]");
        File.WriteAllText(Path.Combine(from, "plugin.log"), "log");

        Assert.Equal(["config.json", "devices.json"], AppPaths.MigrateLegacy(from, to));
        Assert.False(File.Exists(Path.Combine(to, "plugin.log")));

        File.WriteAllText(Path.Combine(to, "config.json"), "{\"new\":1}");
        Assert.Empty(AppPaths.MigrateLegacy(from, to));
        Assert.Equal("{\"new\":1}", File.ReadAllText(Path.Combine(to, "config.json")));
    }

    [Fact]
    public void NothingToMigrateWithoutLegacyConfig()
    {
        using var tmp = new TempDir();
        Assert.Empty(AppPaths.MigrateLegacy(tmp.MakeDir("old"), Path.Combine(tmp.Path, "new")));
    }

    [Fact]
    public void MigrationSkipsFilesAlreadyWrittenMeanwhile()
    {
        using var tmp = new TempDir();
        var from = tmp.MakeDir("old");
        var to = tmp.MakeDir("new");
        File.WriteAllText(Path.Combine(from, "config.json"), "{}");
        File.WriteAllText(Path.Combine(from, "devices.json"), "[]");
        File.WriteAllText(Path.Combine(to, "devices.json"), "[{}]");

        Assert.Equal(["config.json"], AppPaths.MigrateLegacy(from, to));
        Assert.Equal("[{}]", File.ReadAllText(Path.Combine(to, "devices.json")));
    }
}
