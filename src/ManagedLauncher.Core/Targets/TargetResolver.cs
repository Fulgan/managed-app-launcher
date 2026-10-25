using System.Runtime.InteropServices;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Interop;

namespace ManagedLauncher.Core.Targets;

/// <summary>
/// Turns an <see cref="Entry"/> into concrete paths. Called on every press and poll and never cached,
/// so an update that moves an app to a new version folder is picked up immediately.
/// </summary>
public static class TargetResolver
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    public static ResolvedTarget Resolve(Entry entry)
    {
        var target = Expand(entry.Target);
        var kind = entry.Kind ?? TargetKindDetector.Detect(target);
        var resolved = kind switch
        {
            TargetKind.Exe => ResolveExe(target),
            TargetKind.Glob => ResolveGlob(target),
            TargetKind.Lnk => ResolveLnk(target),
            TargetKind.Aumid => ResolveAumid(target),
            _ => ResolvedTarget.Failed(kind, $"unknown kind {kind}"),
        };

        if (!string.IsNullOrWhiteSpace(entry.TrackPath) && resolved.IsLaunchable)
        {
            var track = Expand(entry.TrackPath);
            resolved = resolved with
            {
                MatchPath = GlobResolver.HasWildcard(track) ? GlobResolver.Resolve(track) : Path.GetFullPath(track),
            };
        }
        return resolved;
    }

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static ResolvedTarget ResolveExe(string path) =>
        File.Exists(path)
            ? new(TargetKind.Exe, path, null, Path.GetFullPath(path), null, null, null, path, null)
            : ResolvedTarget.Failed(TargetKind.Exe, $"not found: {path}");

    private static ResolvedTarget ResolveGlob(string pattern)
    {
        var match = GlobResolver.Resolve(pattern);
        return match is null
            ? ResolvedTarget.Failed(TargetKind.Glob, $"no match: {pattern}")
            : new(TargetKind.Glob, match, null, match, null, null, null, match, null);
    }

    private static ResolvedTarget ResolveLnk(string path)
    {
        if (!File.Exists(path))
            return ResolvedTarget.Failed(TargetKind.Lnk, $"not found: {path}");

        ShellLinkInfo info;
        try
        {
            info = ShellLink.Read(path);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return ResolvedTarget.Failed(TargetKind.Lnk, $"unreadable shortcut: {ex.Message}");
        }

        var exe = string.IsNullOrEmpty(info.TargetPath) ? null : Expand(info.TargetPath);
        if (exe is null && info.Aumid is not null)
            return ResolveAumid(info.Aumid) with { LaunchFile = path };

        return new(TargetKind.Lnk, path, info.Aumid, exe is null ? null : Path.GetFullPath(exe),
            exe, info.Arguments, info.WorkingDirectory, path, null);
    }

    private static ResolvedTarget ResolveAumid(string target)
    {
        var aumid = target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? target[AppsFolderPrefix.Length..]
            : target;
        return string.IsNullOrWhiteSpace(aumid)
            ? ResolvedTarget.Failed(TargetKind.Aumid, "empty AUMID")
            : new(TargetKind.Aumid, null, aumid, null, null, null, null, AppsFolderPrefix + aumid, null);
    }
}
