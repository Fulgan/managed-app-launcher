using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Core.Targets;

public static class TargetKindDetector
{
    public static TargetKind Detect(string target)
    {
        var t = target.Trim().Trim('"');
        if (t.StartsWith(TargetResolver.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return TargetKind.Aumid;
        if (IsUrl(t))
            return TargetKind.Url;
        if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return TargetKind.Lnk;
        if (GlobResolver.HasWildcard(t))
            return TargetKind.Glob;
        if (LooksLikeAumid(t))
            return TargetKind.Aumid;
        return TargetKind.Exe;
    }

    /// <summary>A link with a scheme of two letters or more (<c>steam://…</c>); <c>C:\…</c> is a path, not a scheme.</summary>
    private static bool IsUrl(string t)
    {
        var colon = t.IndexOf("://", StringComparison.Ordinal);
        return colon >= 2 && t[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    /// <summary>Packaged AUMIDs are <c>Family_hash!App</c>; desktop AUMIDs are dotted names. Neither has path separators.</summary>
    private static bool LooksLikeAumid(string t) =>
        t.Length > 0
        && t.IndexOfAny(['\\', '/', ':']) < 0
        && (t.Contains('!') || !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
}
