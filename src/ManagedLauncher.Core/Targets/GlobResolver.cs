namespace ManagedLauncher.Core.Targets;

/// <summary>
/// Resolves a path with <c>*</c>/<c>?</c> in any segment to the existing match whose wildcard parts
/// carry the highest version. Only complete matches count, so a half-installed version folder
/// without the final file is ignored.
/// </summary>
public static class GlobResolver
{
    public static bool HasWildcard(string path) => path.AsSpan().IndexOfAny('*', '?') >= 0;

    public static string? Resolve(string pattern) =>
        Matches(pattern).OrderByDescending(p => p, VersionComparer.Instance).FirstOrDefault();

    public static IReadOnlyList<string> Matches(string pattern)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pattern));
        var root = Path.GetPathRoot(full)!;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        List<string> current = [root];
        for (var s = 0; s < segments.Length; s++)
        {
            var last = s == segments.Length - 1;
            current = current.SelectMany(dir => Expand(dir, segments[s], last)).ToList();
        }
        return current;
    }

    private static IEnumerable<string> Expand(string dir, string segment, bool last)
    {
        if (!HasWildcard(segment))
        {
            var next = Path.Combine(dir, segment);
            var exists = last ? File.Exists(next) || Directory.Exists(next) : Directory.Exists(next);
            return exists ? [next] : [];
        }
        try
        {
            return last
                ? Directory.EnumerateFileSystemEntries(dir, segment).ToList()
                : Directory.EnumerateDirectories(dir, segment).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
