using ManagedLauncher.Core.Config;

namespace ManagedLauncher.Core.Targets;

/// <param name="Kind">Effective kind; a shortcut that only carries an AUMID resolves as <see cref="TargetKind.Aumid"/>.</param>
/// <param name="LaunchFile">File handed to ShellExecute (<c>.lnk</c> or exe). Null for AUMID targets.</param>
/// <param name="Aumid">Set for AUMID targets, and for shortcuts that declare one.</param>
/// <param name="MatchPath">Full exe path used to detect running instances.</param>
/// <param name="ShortcutExe">For shortcuts: the exe the shortcut points to.</param>
/// <param name="ShortcutArgs">For shortcuts: the arguments stored in the shortcut.</param>
/// <param name="ShortcutWorkDir">For shortcuts: the working directory stored in the shortcut.</param>
/// <param name="IconSource">Path or <c>shell:AppsFolder\…</c> name to extract the icon from.</param>
/// <param name="Error">Why the target can't be launched; null when it can.</param>
public sealed record ResolvedTarget(
    TargetKind Kind,
    string? LaunchFile,
    string? Aumid,
    string? MatchPath,
    string? ShortcutExe,
    string? ShortcutArgs,
    string? ShortcutWorkDir,
    string? IconSource,
    string? Error)
{
    public bool IsLaunchable => Error is null;

    public static ResolvedTarget Failed(TargetKind kind, string error) =>
        new(kind, null, null, null, null, null, null, null, error);
}
