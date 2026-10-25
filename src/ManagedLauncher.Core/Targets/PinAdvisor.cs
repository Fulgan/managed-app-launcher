using System.Text.RegularExpressions;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Interop;

namespace ManagedLauncher.Core.Targets;

/// <summary>Builds the entry for a pinned file and spots targets that will break on the next app update.</summary>
public static partial class PinAdvisor
{
    /// <summary>
    /// A run of at least two numbers joined by <c>.</c> or <c>_</c>: <c>1.0.9</c>, <c>2024.3</c>, <c>10_2</c>.
    /// Hyphens don't join, so names like <c>Office16-2</c> aren't taken for versions.
    /// </summary>
    [GeneratedRegex(@"\d+(?:[._]\d+)+")]
    private static partial Regex VersionRun();

    [GeneratedRegex(@"--processStart\s+(?:""(?<exe>[^""]+)""|(?<exe>\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();

    /// <summary>
    /// Replaces version-like runs in directory segments with <c>*</c>. Returns null when no directory carries a
    /// version, or when the pattern would not resolve back to the same file.
    /// </summary>
    public static string? SuggestGlob(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        var dir = Path.GetDirectoryName(full);
        if (dir is null)
            return null;

        var root = Path.GetPathRoot(dir)!;
        var segments = dir[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var replaced = VersionRun().Replace(segments[i], "*");
            changed |= replaced != segments[i];
            segments[i] = replaced;
        }
        if (!changed)
            return null;

        var pattern = Path.Combine([root, .. segments, Path.GetFileName(full)]);
        return GlobResolver.Matches(pattern).Contains(full, StringComparer.OrdinalIgnoreCase) ? pattern : null;
    }

    /// <summary>
    /// Squirrel installers (Discord, Teams classic, GitHub Desktop…) start apps through <c>Update.exe --processStart X</c>,
    /// which exits at once; the real process is <c>app-&lt;version&gt;\X</c> next to it.
    /// </summary>
    public static string? SquirrelAppPattern(string exePath, string? args)
    {
        if (!string.Equals(Path.GetFileName(exePath), "Update.exe", StringComparison.OrdinalIgnoreCase) || args is null)
            return null;
        var match = ProcessStart().Match(args);
        return match.Success
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!, "app-*", match.Groups["exe"].Value)
            : null;
    }

    public sealed record PinPlan(Entry Entry, IReadOnlyList<string> Advice);

    /// <summary>
    /// The entry keeps the path as given; a shortcut stays a shortcut because installers rewrite their shortcuts on
    /// update. Only the running-indicator path is adjusted for Squirrel launchers. Everything else is advice.
    /// </summary>
    public static PinPlan Plan(string path, string? title, string? preset)
    {
        var target = TargetResolver.Expand(path);
        var kind = TargetKindDetector.Detect(target);
        if (kind is TargetKind.Exe or TargetKind.Lnk)
            target = Path.GetFullPath(target);

        var entry = new Entry
        {
            Target = target,
            Kind = kind,
            Title = title,
            Preset = preset ?? AppConfig.DefaultPresetName,
        };
        var advice = new List<string>();

        string? exe = null, args = null;
        if (kind == TargetKind.Lnk && File.Exists(target))
        {
            var link = ShellLink.Read(target);
            exe = link.TargetPath is null ? null : TargetResolver.Expand(link.TargetPath);
            args = link.Arguments;
            if (exe is null && link.Aumid is not null)
                advice.Add($"shortcut opens a packaged app; to pin it directly: malaunch pin {link.Aumid}");
        }
        else if (kind == TargetKind.Exe)
        {
            exe = target;
        }

        if (exe is not null)
        {
            if (SquirrelAppPattern(exe, args) is { } squirrel)
            {
                entry.TrackPath = squirrel;
                advice.Add($"Squirrel launcher: running indicator tracks {squirrel}");
            }
            else if (SuggestGlob(exe) is { } glob)
            {
                advice.Add(kind == TargetKind.Lnk
                    ? $"shortcut points into a versioned folder; if the installer doesn't update it, pin \"{glob}\" instead"
                    : $"exe sits in a versioned folder and will break on update; pin \"{glob}\" instead");
            }
        }
        return new PinPlan(entry, advice);
    }
}
