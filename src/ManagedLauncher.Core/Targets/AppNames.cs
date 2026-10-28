using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;

namespace ManagedLauncher.Core.Targets;

public static class AppNames
{
    public static string Default(Entry entry)
    {
        var target = entry.Target.Trim('"');
        if (target.StartsWith(TargetResolver.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            target = target[TargetResolver.AppsFolderPrefix.Length..];
        if (TargetKindDetector.Detect(target) == TargetKind.Aumid)
            return ShellItems.GetDisplayName(TargetResolver.AppsFolderPrefix + target) ?? target;
        return Path.GetFileNameWithoutExtension(target);
    }
}
