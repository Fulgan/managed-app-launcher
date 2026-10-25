using System.Text.Json;
using System.Text.RegularExpressions;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Import;

public enum ImportStatus { Ok, Remapped, Broken, Skipped }

/// <param name="Position">Key position as Stream Deck writes it: <c>column,row</c>.</param>
/// <param name="Page">1-based page the key was on.</param>
public sealed record ImportedKey(int Page, string Position, string ActionUuid, Entry? Entry, ImportStatus Status, IReadOnlyList<string> Notes);

/// <summary>
/// Converts launcher buttons from Stream Deck pages into app list entries. Reads profile files only; the
/// Stream Deck application's files are never written.
/// </summary>
public static partial class ProfileImporter
{
    public const string AdvancedLauncher = "com.barraider.advancedlauncher";
    public const string ElgatoOpen = "com.elgato.streamdeck.system.open";
    public const string ElgatoOpenApp = "com.elgato.streamdeck.system.openapp";
    public const string StoreLauncher = "com.barraider.msstorelauncher";
    public const string MultiAction = "com.elgato.streamdeck.multiactions.routine";
    public const string WindowsCockpit = "com.vivremotion.windowscockpit.plugin";

    /// <summary>Windows Cockpit event code that opens the Stream Deck application; its other codes are system commands, not apps.</summary>
    private const string CockpitStreamDeckInterface = "#004";

    [GeneratedRegex(@"^(?<users>[A-Za-z]:\\Users\\)(?<user>[^\\]+)(?<rest>\\.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePath();

    /// <param name="storeApps">Start menu apps as (AUMID, display name), used to map Store launcher buttons that only record a name.</param>
    /// <summary>Every page of the profile in page order; within a page, keys in reading order (row, then column).</summary>
    public static IReadOnlyList<ImportedKey> ReadProfile(ProfileInfo profile, Func<IReadOnlyList<(string Aumid, string Name)>> storeApps)
    {
        var result = new List<ImportedKey>();
        for (var page = 1; page <= profile.Pages.Count; page++)
        {
            if (StreamDeckProfiles.PageManifest(profile, page) is { } manifest)
                result.AddRange(ReadPage(manifest, page, storeApps));
        }
        return result;
    }

    public static IReadOnlyList<ImportedKey> ReadPage(string pageManifestPath, int page, Func<IReadOnlyList<(string Aumid, string Name)>> storeApps)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pageManifestPath));
        var result = new List<(int Row, int Col, ImportedKey Key)>();
        if (!doc.RootElement.TryGetProperty("Controllers", out var controllers) || controllers.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var controller in controllers.EnumerateArray())
        {
            if (controller.TryGetProperty("Type", out var type) && type.GetString() != "Keypad")
                continue;
            if (!controller.TryGetProperty("Actions", out var actions) || actions.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var key in actions.EnumerateObject())
            {
                var parts = key.Name.Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var col) || !int.TryParse(parts[1], out var row))
                    continue;
                result.Add((row, col, ImportKey(page, key.Name, key.Value, storeApps)));
            }
        }
        return result.OrderBy(k => k.Row).ThenBy(k => k.Col).Select(k => k.Key).ToList();
    }

    private static ImportedKey ImportKey(int page, string position, JsonElement action, Func<IReadOnlyList<(string, string)>> storeApps)
    {
        var uuid = Str(action, "UUID") ?? "";
        var notes = new List<string>();
        var source = action;

        if (uuid == MultiAction)
        {
            var inner = FindLauncherInMultiAction(action);
            if (inner is null)
                return new ImportedKey(page, position, uuid, null, ImportStatus.Skipped, ["multi-action without a launcher"]);
            source = inner.Value;
            notes.Add($"launcher taken from multi-action ({Str(source, "UUID")}); other steps dropped");
        }

        var entry = MapAction(source, notes, storeApps);
        if (entry is null)
            return new ImportedKey(page, position, uuid, null, ImportStatus.Skipped, notes.Count > 0 ? notes : ["not a launcher"]);

        var title = FirstStateTitle(action);
        if (!string.IsNullOrWhiteSpace(title))
            entry.Title = title.Replace('\n', ' ').Trim();

        var status = CheckTarget(entry, notes);
        return new ImportedKey(page, position, uuid, entry, status, notes);
    }

    private static Entry? MapAction(JsonElement action, List<string> notes, Func<IReadOnlyList<(string Aumid, string Name)>> storeApps)
    {
        var settings = action.TryGetProperty("Settings", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        string? Setting(string name) => settings.ValueKind == JsonValueKind.Object ? Str(settings, name) : null;
        bool? Flag(string name) =>
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;

        switch (Str(action, "UUID"))
        {
            case AdvancedLauncher:
                return FromExe(Setting("application"), Setting("appArguments"), Setting("appStartIn"), Flag("bringToFront"), Flag("runAsAdmin"), notes);
            case ElgatoOpenApp:
                return FromExe(Setting("exec") ?? Setting("bundle_path"), Setting("args"), null, Flag("bring_to_front"), null, notes);
            case ElgatoOpen:
                var path = Setting("path")?.Trim().Trim('"');
                if (path is null || !(path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                {
                    notes.Add($"opens a file or URL, not an app: {path}");
                    return null;
                }
                return FromExe(path, null, null, null, null, notes);
            case WindowsCockpit:
                var code = Setting("execute_event_code");
                if (code != CockpitStreamDeckInterface)
                {
                    notes.Add($"Windows Cockpit {code} is a system command, not an app");
                    return null;
                }
                notes.Add("Windows Cockpit 'Stream Deck Interface' → StreamDeck.exe");
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return FromExe(Path.Combine(programFiles, "Elgato", "StreamDeck", "StreamDeck.exe"), null, null, true, null, notes);
            case StoreLauncher:
                var name = Setting("applicationId");
                var app = storeApps().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (app.Aumid is null)
                {
                    notes.Add($"no installed app named '{name}'");
                    return null;
                }
                notes.Add($"store app '{name}' → {app.Aumid}");
                return new Entry { Target = app.Aumid, Kind = TargetKind.Aumid, OnRunning = OnRunning.Auto };
            default:
                return null;
        }
    }

    private static Entry? FromExe(string? exe, string? args, string? workDir, bool? bringToFront, bool? admin, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return null;
        exe = exe.Trim().Trim('"').Replace('/', '\\');
        args = string.IsNullOrWhiteSpace(args) ? null : args.Trim();

        var entry = new Entry
        {
            Target = exe,
            Kind = TargetKindDetector.Detect(exe),
            Args = args,
            WorkDir = string.IsNullOrWhiteSpace(workDir) ? null : workDir,
            OnRunning = bringToFront is null ? null : bringToFront.Value ? OnRunning.Auto : OnRunning.Relaunch,
            Admin = admin == true ? true : null,
        };

        if (PinAdvisor.SquirrelAppPattern(exe, args) is { } app)
        {
            notes.Add($"Squirrel launcher → {app}");
            entry.Target = app;
            entry.Kind = TargetKind.Glob;
            entry.Args = null;
            entry.WorkDir = null;
        }
        return entry;
    }

    /// <summary>
    /// A path under another Windows user's profile (profiles copied between accounts) is rewritten to the current
    /// profile when the file exists there; otherwise the entry is kept and flagged.
    /// </summary>
    private static ImportStatus CheckTarget(Entry entry, List<string> notes)
    {
        if (TargetResolver.Resolve(entry).IsLaunchable)
            return ImportStatus.Ok;

        var match = UserProfilePath().Match(entry.Target);
        if (match.Success)
        {
            var mine = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidate = mine + match.Groups["rest"].Value;
            var trial = new Entry { Target = candidate, Kind = entry.Kind };
            if (!string.Equals(match.Groups["users"].Value + match.Groups["user"].Value, mine, StringComparison.OrdinalIgnoreCase)
                && TargetResolver.Resolve(trial).IsLaunchable)
            {
                notes.Add($"remapped from {entry.Target}");
                entry.Target = candidate;
                if (entry.WorkDir is not null && UserProfilePath().Match(entry.WorkDir) is { Success: true } wd)
                    entry.WorkDir = mine + wd.Groups["rest"].Value;
                return ImportStatus.Remapped;
            }
        }
        notes.Add($"target missing: {entry.Target}");
        return ImportStatus.Broken;
    }

    private static JsonElement? FindLauncherInMultiAction(JsonElement action)
    {
        if (!action.TryGetProperty("Actions", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("Actions", out var steps) || steps.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var step in steps.EnumerateArray())
            {
                if (Str(step, "UUID") is AdvancedLauncher or ElgatoOpenApp or ElgatoOpen or StoreLauncher)
                    return step;
            }
        }
        return null;
    }

    private static string? FirstStateTitle(JsonElement action) =>
        action.TryGetProperty("States", out var states) && states.ValueKind == JsonValueKind.Array && states.GetArrayLength() > 0
            ? Str(states[0], "Title")
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
