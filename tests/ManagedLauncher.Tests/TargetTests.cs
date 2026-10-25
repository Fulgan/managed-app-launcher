using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Tests;

public class TargetTests
{
    [Theory]
    [InlineData("app-1.0.10", "app-1.0.9")]
    [InlineData("app-1.10.0", "app-1.9.99")]
    [InlineData("2024.3", "2024.2.5")]
    [InlineData("v010", "v9")]
    [InlineData("app-1.0.9b", "app-1.0.9a")]
    public void VersionComparerOrdersNumbersByValue(string higher, string lower)
    {
        Assert.True(VersionComparer.Instance.Compare(higher, lower) > 0);
        Assert.True(VersionComparer.Instance.Compare(lower, higher) < 0);
    }

    [Fact]
    public void VersionComparerTreatsCaseAndLeadingZerosAsEqual()
    {
        Assert.Equal(0, VersionComparer.Instance.Compare("App-01.2", "app-1.2"));
    }

    [Fact]
    public void GlobPicksHighestNumericVersionNotLexical()
    {
        using var tmp = new TempDir();
        tmp.Touch("Discord", "app-1.0.9", "Discord.exe");
        var expected = tmp.Touch("Discord", "app-1.0.10", "Discord.exe");
        tmp.Touch("Discord", "app-1.0.2", "Discord.exe");

        var match = GlobResolver.Resolve(Path.Combine(tmp.Path, "Discord", "app-*", "Discord.exe"));

        Assert.Equal(expected, match);
    }

    [Fact]
    public void GlobIgnoresVersionFolderMissingTheFile()
    {
        using var tmp = new TempDir();
        var expected = tmp.Touch("App", "app-1.0.9", "App.exe");
        tmp.MakeDir("App", "app-1.0.10");

        Assert.Equal(expected, GlobResolver.Resolve(Path.Combine(tmp.Path, "App", "app-*", "App.exe")));
    }

    [Fact]
    public void GlobWithNoMatchFailsWithoutThrowing()
    {
        using var tmp = new TempDir();

        var resolved = TargetResolver.Resolve(new Entry { Target = Path.Combine(tmp.Path, "none-*", "x.exe") });

        Assert.Equal(TargetKind.Glob, resolved.Kind);
        Assert.False(resolved.IsLaunchable);
    }

    [Fact]
    public void ResolveReadsDiskEachCall()
    {
        using var tmp = new TempDir();
        var pattern = Path.Combine(tmp.Path, "app-*", "a.exe");
        var entry = new Entry { Target = pattern };
        tmp.Touch("app-1.0", "a.exe");
        var first = TargetResolver.Resolve(entry).LaunchFile;

        var updated = tmp.Touch("app-1.1", "a.exe");

        Assert.NotEqual(first, TargetResolver.Resolve(entry).LaunchFile);
        Assert.Equal(updated, TargetResolver.Resolve(entry).LaunchFile);
    }

    [Fact]
    public void TrackPathOverridesMatchPathAndSupportsGlob()
    {
        using var tmp = new TempDir();
        var stub = tmp.Touch("Discord", "Update.exe");
        var real = tmp.Touch("Discord", "app-1.0.5", "Discord.exe");

        var resolved = TargetResolver.Resolve(new Entry
        {
            Target = stub,
            TrackPath = Path.Combine(tmp.Path, "Discord", "app-*", "Discord.exe"),
        });

        Assert.Equal(stub, resolved.LaunchFile);
        Assert.Equal(real, resolved.MatchPath);
    }

    [Fact]
    public void EnvironmentVariablesExpand()
    {
        using var tmp = new TempDir();
        var exe = tmp.Touch("x.exe");
        Environment.SetEnvironmentVariable("MALAUNCH_TEST_DIR", tmp.Path);

        var resolved = TargetResolver.Resolve(new Entry { Target = @"%MALAUNCH_TEST_DIR%\x.exe" });

        Assert.Equal(exe, resolved.LaunchFile);
    }

    [Fact]
    public void MissingExeFails()
    {
        var resolved = TargetResolver.Resolve(new Entry { Target = @"C:\definitely\not\here.exe" });

        Assert.Equal(TargetKind.Exe, resolved.Kind);
        Assert.NotNull(resolved.Error);
    }

    [Theory]
    [InlineData(@"C:\a\b.lnk", TargetKind.Lnk)]
    [InlineData(@"C:\a\app-*\b.exe", TargetKind.Glob)]
    [InlineData(@"C:\a\b.exe", TargetKind.Exe)]
    [InlineData(@"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams", TargetKind.Aumid)]
    [InlineData("MSTeams_8wekyb3d8bbwe!MSTeams", TargetKind.Aumid)]
    [InlineData("Microsoft.WindowsTerminal", TargetKind.Aumid)]
    [InlineData("\"C:\\Program Files\\x.exe\"", TargetKind.Exe)]
    public void DetectsKind(string target, TargetKind kind)
    {
        Assert.Equal(kind, TargetKindDetector.Detect(target));
    }

    [Fact]
    public void AumidResolvesToAppsFolderIconSource()
    {
        var resolved = TargetResolver.Resolve(new Entry { Target = "MSTeams_8wekyb3d8bbwe!MSTeams" });

        Assert.Equal("MSTeams_8wekyb3d8bbwe!MSTeams", resolved.Aumid);
        Assert.Equal(@"shell:AppsFolder\MSTeams_8wekyb3d8bbwe!MSTeams", resolved.IconSource);
        Assert.Null(resolved.MatchPath);
    }

    [Fact]
    public void ShortcutResolvesToTargetExeAndLaunchesTheShortcut()
    {
        using var tmp = new TempDir();
        var exe = tmp.Touch("app-2.0", "tool.exe");
        var lnk = tmp.File("Tool.lnk");
        CreateShortcut(lnk, exe, "--flag", tmp.Path);

        var resolved = TargetResolver.Resolve(new Entry { Target = lnk });

        Assert.Equal(TargetKind.Lnk, resolved.Kind);
        Assert.Equal(lnk, resolved.LaunchFile);
        Assert.Equal(exe, resolved.MatchPath, ignoreCase: true);
        Assert.Equal("--flag", resolved.ShortcutArgs);
    }

    internal static void CreateShortcut(string lnk, string target, string args, string workDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(lnk);
        shortcut.TargetPath = target;
        shortcut.Arguments = args;
        shortcut.WorkingDirectory = workDir;
        shortcut.Save();
    }
}
