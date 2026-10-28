using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;

namespace ManagedLauncher.Core.Targets;

public sealed record AppChoice(string Name, string Detail, PinAdvisor.PinPlan Plan)
{
    public Entry Entry => Plan.Entry;
}

/// <summary>Apps a person can pick instead of typing a path: Start menu, Steam library, windows open right now.</summary>
public static class AppCatalog
{
    public const string StartMenuSource = "start";
    public const string SteamSource = "steam";
    public const string RunningSource = "running";

    public static IReadOnlyList<AppChoice> List(string source) => source switch
    {
        StartMenuSource => StartMenu(),
        SteamSource => Steam(),
        RunningSource => Running(Environment.ProcessId),
        _ => throw new ArgumentException($"unknown app source {source}", nameof(source)),
    };

    public static IReadOnlyList<AppChoice> StartMenu() =>
        Shortcuts().Concat(PackagedApps())
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<AppChoice> Steam() =>
        SteamLibrary.Games().Select(g => new AppChoice(g.Name, g.InstallDir, new PinAdvisor.PinPlan(g.ToEntry(), []))).ToList();

    public static IReadOnlyList<AppChoice> Running(int selfPid)
    {
        var names = ShellItems.EnumerateApps()
            .GroupBy(a => a.Aumid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var apps = RunningApps.Collect(WindowFinder.VisibleWindows(), new Win32ProcessSource().Snapshot(), names, selfPid, Describe);
        return apps.Select(a => Choice(a.Name, a.Target, a.Detail)).ToList();
    }

    private static AppChoice Choice(string name, string target, string detail) =>
        new(name, detail, PinAdvisor.Plan(target, null));

    private static string? Describe(string exe)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<AppChoice> Shortcuts()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ];
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return Choice(name, file, Path.GetRelativePath(root, Path.GetDirectoryName(file)!));
            }
        }
    }

    private static IEnumerable<AppChoice> PackagedApps() =>
        ShellItems.EnumerateApps()
            .Where(a => a.Aumid.Contains('!'))
            .Select(a => Choice(a.Name, a.Aumid, "Store app"));
}
