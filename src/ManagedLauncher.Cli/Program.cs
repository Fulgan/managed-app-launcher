using System.Runtime.InteropServices;
using ManagedLauncher.Core;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Import;
using ManagedLauncher.Core.Paging;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Cli;

internal static class Program
{
    private const string Usage = """
        usage: malaunch <command>

          pin <path|AUMID> [--list l] [--at n] [--title t] [--notify]
          break row|page [--list l] [--at n]
          folder <name> [--list l] [--at n]
                 [--auto steam|startmenu|running|directory] [--path dir] [--types exe,lnk,symlink,bat]
          link <list> [--list l] [--at n] [--title t]
          unpin <index> [--list l]
          move <from> <to> [--list l]
          list [--list l]
          lists | lists add <name> | lists rm <list> | lists rename <list> <new name>
          set <index> [--list l] [--args a] [--workdir d] [--admin|--no-admin]
                      [--on-running auto|focus|relaunch|none] [--kill-mode all|newest]
                      [--indicator border|dot] [--long-press auto|none]
          import <profile uuid|name> [--list name] [--append] [--dry-run]
          page --out file [--name n] [--device model] [--size CxR]
          shell install|uninstall

        lists are named by their path from the top, e.g. Games/Steam, or by id
        config: %APPDATA%\ManagedAppLauncher\config.json
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths.MigrateLegacy();
        var notify = args.Contains("--notify", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            return args[0].ToLowerInvariant() switch
            {
                "pin" => Pin(new CommandLine(args.Skip(1), "notify")),
                "break" => Break(new CommandLine(args.Skip(1))),
                "folder" => Folder(new CommandLine(args.Skip(1))),
                "link" => Link(new CommandLine(args.Skip(1))),
                "unpin" => Unpin(new CommandLine(args.Skip(1))),
                "move" => Move(new CommandLine(args.Skip(1))),
                "list" => List(new CommandLine(args.Skip(1))),
                "lists" => Lists(new CommandLine(args.Skip(1))),
                "set" => Set(new CommandLine(args.Skip(1), "admin", "no-admin")),
                "import" => Import(new CommandLine(args.Skip(1), "dry-run", "append")),
                "page" => Page(new CommandLine(args.Skip(1))),
                "profiles" => Profiles(new CommandLine(args.Skip(1))),
                "shell" => Shell(new CommandLine(args.Skip(1))),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            Report(notify, $"{ex.Message}\n\n{Usage}", error: true);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or COMException)
        {
            Report(notify, ex.Message, error: true);
            return 1;
        }
    }

    private static int Pin(CommandLine cl)
    {
        var path = cl.Arg(0, "path");
        var plan = PinAdvisor.Plan(path, cl.Get("title"));
        var resolved = TargetResolver.Resolve(plan.Entry);
        var at = cl.GetInt("at");
        var index = -1;
        ConfigStore.Update(AppPaths.ConfigFile, c => index = c.Insert(ListArg(c, cl), plan.Entry, at));

        var lines = new List<string> { $"pinned {plan.Entry.Title ?? Path.GetFileNameWithoutExtension(plan.Entry.Target)} at {index}" };
        if (!resolved.IsLaunchable)
            lines.Add($"warning: {resolved.Error}");
        lines.AddRange(plan.Advice.Select(a => "note: " + a));

        if (cl.Has("notify"))
        {
            Report(true, string.Join('\n', lines), error: !resolved.IsLaunchable);
        }
        else
        {
            Console.WriteLine(index);
            foreach (var line in lines.Skip(1))
                Console.Error.WriteLine(line);
        }
        return 0;
    }

    /// <summary>Inserts a row or page break: the next app starts on a new row or page of the deck.</summary>
    private static int Break(CommandLine cl)
    {
        var kind = cl.Arg(0, "row|page").ToLowerInvariant() switch
        {
            "row" => ListBreak.Row,
            "page" => ListBreak.Page,
            var other => throw new UsageException($"unknown break '{other}', use row or page"),
        };
        var index = -1;
        ConfigStore.Update(AppPaths.ConfigFile, c => index = c.Insert(ListArg(c, cl), new Entry { Break = kind }, cl.GetInt("at")));
        Console.WriteLine(index);
        return 0;
    }

    private static int Folder(CommandLine cl)
    {
        var name = cl.Arg(0, "folder name");
        var source = AutoSource(cl);
        var index = -1;
        ConfigStore.Update(AppPaths.ConfigFile, c =>
        {
            var parent = ExistingList(c, cl.Get("list"));
            var problem = source is null
                ? c.AddFolder(parent, name, out _, out index, cl.GetInt("at"))
                : c.AddAutoFolder(parent, name, source, out _, out index, cl.GetInt("at"));
            if (problem is not null)
                throw new UsageException(problem);
        });
        Console.WriteLine(index);
        return 0;
    }

    /// <summary>The auto-folder source given by <c>--auto</c>, <c>--path</c> and <c>--types</c>; null for a plain folder.</summary>
    private static ListSource? AutoSource(CommandLine cl)
    {
        if (cl.Get("auto") is not { } auto)
            return cl.Has("path") || cl.Has("types") ? throw new UsageException("--path and --types need --auto directory") : null;
        var type = auto.ToLowerInvariant() switch
        {
            "steam" => SourceType.Steam,
            "startmenu" => SourceType.StartMenu,
            "running" => SourceType.Running,
            "directory" => SourceType.Directory,
            _ => throw new UsageException("--auto: steam|startmenu|running|directory"),
        };
        var path = cl.Get("path");
        if (type == SourceType.Directory && string.IsNullOrWhiteSpace(path))
            throw new UsageException("--auto directory needs --path");
        var types = cl.Get("types")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToList();
        if (types?.FirstOrDefault(t => !AutoFolders.AllTypes.Contains(t)) is { } unknown)
            throw new UsageException($"unknown type '{unknown}', use {string.Join(",", AutoFolders.AllTypes)}");
        return type == SourceType.Directory
            ? new ListSource { Type = type, Path = path!.Contains('%') ? path : Path.GetFullPath(path), Types = types }
            : new ListSource { Type = type };
    }

    private static int Link(CommandLine cl)
    {
        var target = cl.Arg(0, "list to open");
        var index = -1;
        ConfigStore.Update(AppPaths.ConfigFile, c =>
        {
            if (c.AddLink(ExistingList(c, cl.Get("list")), ExistingList(c, target), out index, cl.GetInt("at"), cl.Get("title")) is { } problem)
                throw new UsageException(problem);
        });
        Console.WriteLine(index);
        return 0;
    }

    /// <summary>The <c>--list</c> option; a plain name that matches no list creates a top-level list, a path must exist.</summary>
    private static string? ListArg(AppConfig config, CommandLine cl)
    {
        var list = cl.Get("list");
        return list is not null && (list.Contains('/') || config.Exists(list)) ? EditableList(config, list) : list;
    }

    private static string ExistingList(AppConfig config, string? list) =>
        config.Exists(list) ? config.ResolveList(list) : throw new UsageException($"no list '{list}'");

    /// <summary>A list whose items can be edited: existing and not an auto-folder.</summary>
    private static string EditableList(AppConfig config, string? list) =>
        config.IsDynamic(ExistingList(config, list)) ? throw new UsageException(AppConfig.FillsItself) : config.ResolveList(list);

    private static int Unpin(CommandLine cl)
    {
        var index = Index(cl.Arg(0, "index"));
        var removed = false;
        ConfigStore.Update(AppPaths.ConfigFile, c => removed = c.Remove(EditableList(c, cl.Get("list")), index));
        if (!removed)
            Console.Error.WriteLine($"no item at {index}");
        return removed ? 0 : 1;
    }

    private static int Move(CommandLine cl)
    {
        var from = Index(cl.Arg(0, "from"));
        var to = Index(cl.Arg(1, "to"));
        var moved = false;
        ConfigStore.Update(AppPaths.ConfigFile, c => moved = c.Move(EditableList(c, cl.Get("list")), from, to));
        if (!moved)
            Console.Error.WriteLine($"can't move {from} to {to}");
        return moved ? 0 : 1;
    }

    private static int List(CommandLine cl)
    {
        var config = ConfigStore.Load(AppPaths.ConfigFile);
        var listId = ExistingList(config, cl.Get("list"));
        var items = config.IsDynamic(listId) ? config.Generate(listId, new SystemAppSources()) : config.GetList(listId);
        var rows = items
            .Select((e, i) => e.Break is { } b
                ? [i.ToString(), $"--- {b.ToString().ToLowerInvariant()} break ---", "", "", ""]
                : e.Opens is { } opens
                ? [i.ToString(), config.TitleOf(e), e.IsFolder ? "folder" : "link", config.PathOf(opens), e.LinkProblem ?? ""]
                : new[]
                {
                    i.ToString(),
                    e.Title ?? "",
                    (e.Kind ?? TargetKindDetector.Detect(e.Target)).ToString().ToLowerInvariant(),
                    e.Target,
                    TargetResolver.Resolve(e).Error ?? "",
                })
            .ToList();
        Table(["index", "title", "kind", "target", "problem"], rows);
        return 0;
    }

    private static int Lists(CommandLine cl)
    {
        if (cl.Positional.Count == 0)
        {
            var config = ConfigStore.Load(AppPaths.ConfigFile);
            Table(["list", "items", "id"], config.Tree()
                .Select(t => new[] { new string(' ', (t.Depth - 1) * 2) + config.NameOf(t.Id), config.GetList(t.Id).Count.ToString(), t.Id })
                .ToList());
            return 0;
        }
        var name = cl.Arg(1, "list");
        switch (cl.Arg(0, "add|rm|rename").ToLowerInvariant())
        {
            case "add":
                string? added = null;
                ConfigStore.Update(AppPaths.ConfigFile, c => added = c.AddList(name));
                if (added is null)
                    Console.Error.WriteLine($"a list named '{name}' already exists");
                return added is null ? 1 : 0;
            case "rm":
                ConfigStore.Update(AppPaths.ConfigFile, c =>
                {
                    var id = ExistingList(c, name);
                    if (id == c.HomeList)
                        throw new UsageException($"'{c.NameOf(id)}' is the home list (shown when a key names no list) and can't be removed");
                    c.RemoveList(id);
                });
                return 0;
            case "rename":
                var to = cl.Arg(2, "new name");
                ConfigStore.Update(AppPaths.ConfigFile, c =>
                {
                    if (c.RenameList(ExistingList(c, name), to) is { } error)
                        throw new UsageException(error);
                });
                return 0;
            default:
                throw new UsageException("lists add <name> | lists rm <list> | lists rename <list> <new name>");
        }
    }

    /// <summary>Changes the launch settings of one app; options not given stay as they are.</summary>
    private static int Set(CommandLine cl)
    {
        var index = Index(cl.Arg(0, "index"));
        var found = false;
        ConfigStore.Update(AppPaths.ConfigFile, c =>
        {
            if (c.GetApp(EditableList(c, cl.Get("list")), index) is not { } e)
                return;
            found = true;
            if (cl.Has("args")) e.Args = Blank(cl.Get("args"));
            if (cl.Has("workdir")) e.WorkDir = Blank(cl.Get("workdir"));
            if (cl.Has("admin")) e.Admin = true;
            if (cl.Has("no-admin")) e.Admin = false;
            e.OnRunning = cl.GetEnum<OnRunning>("on-running") ?? e.OnRunning;
            e.KillMode = cl.GetEnum<KillMode>("kill-mode") ?? e.KillMode;
            e.Indicator = cl.GetEnum<IndicatorStyle>("indicator") ?? e.Indicator;
            e.LongPress = cl.GetEnum<LongPressMode>("long-press") ?? e.LongPress;
        });
        if (!found)
            throw new UsageException($"no app at index {index}");
        return 0;
    }

    private static int Import(CommandLine cl)
    {
        var id = cl.Arg(0, "profile uuid or name");
        var profile = StreamDeckProfiles.Find(id) ?? throw new UsageException($"no Stream Deck profile '{id}'");

        IReadOnlyList<(string, string)>? apps = null;
        var keys = ProfileImporter.ReadProfile(profile,
            () => apps ??= ShellItems.EnumerateApps().Select(a => (a.Aumid, a.Name)).ToList());

        Console.WriteLine($"{profile.Name}, {profile.Pages.Count} page(s):");
        Table(["page", "key", "status", "title", "target", "notes"], keys.Select(k => new[]
        {
            k.Page.ToString(),
            k.Position,
            k.Status.ToString().ToLowerInvariant(),
            k.Entry?.Title ?? "",
            k.Entry?.Target ?? k.ActionUuid,
            string.Join("; ", k.Notes),
        }).ToList());

        var imported = keys.Where(k => k.Entry is not null).Select(k => k.Entry!).ToList();
        var append = cl.Has("append");
        if (cl.Has("dry-run"))
        {
            Console.WriteLine($"\ndry run: {imported.Count} apps would be {(append ? "appended" : "written")}");
            return 0;
        }
        ConfigStore.Update(AppPaths.ConfigFile, c =>
        {
            var list = c.EnsureList(ListArg(c, cl));
            if (!append)
                list.Clear();
            list.AddRange(imported);
        });
        Console.WriteLine($"\n{(append ? "appended" : "wrote")} {imported.Count} apps to {AppPaths.ConfigFile}");
        return 0;
    }

    private static int Page(CommandLine cl)
    {
        var output = cl.Get("out") ?? throw new UsageException("--out is required");
        var (columns, rows) = ParseSize(cl.Get("size") ?? "5x3");
        var model = cl.Get("device") ?? "20GBA9901";
        var name = cl.Get("name") ?? "Managed App Launcher";
        // Stream Deck treats a profile action whose plugin version differs from the installed one as a missing
        // plugin. The assembly version equals the manifest Version; the manifest itself is not read at runtime.
        var version = typeof(Program).Assembly.GetName().Version!.ToString();
        File.WriteAllBytes(output, SlotPageBuilder.Build(name, model, null, columns, rows, version));
        Console.WriteLine($"wrote {output} ({columns}x{rows}, {model})");
        return 0;
    }

    /// <summary>
    /// Build step: writes one launcher profile per supported model into <c>--out</c> and replaces the <c>Profiles</c>
    /// array of the plugin manifest given by <c>--manifest</c>.
    /// </summary>
    private static int Profiles(CommandLine cl)
    {
        var output = cl.Get("out") ?? throw new UsageException("--out is required");
        var manifestPath = cl.Get("manifest") ?? throw new UsageException("--manifest is required");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var version = manifest["Version"]?.GetValue<string>() ?? typeof(Program).Assembly.GetName().Version!.ToString();
        Directory.CreateDirectory(output);
        foreach (var profile in LauncherProfiles.All)
        {
            var file = Path.Combine(output, profile.Name + ".streamDeckProfile");
            File.WriteAllBytes(file, SlotPageBuilder.Build("Managed App Launcher", profile.Model, null, profile.Columns, profile.Rows, version));
            Console.WriteLine($"wrote {file} ({profile.Columns}x{profile.Rows}, {profile.Model})");
        }
        manifest["Profiles"] = LauncherProfiles.ManifestEntries();
        File.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static (int Columns, int Rows) ParseSize(string size)
    {
        var parts = size.ToLowerInvariant().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var r) && c > 0 && r > 0
            ? (c, r)
            : throw new UsageException("--size must look like 5x3");
    }

    private static int Shell(CommandLine cl)
    {
        switch (cl.Arg(0, "install|uninstall").ToLowerInvariant())
        {
            case "install":
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("unknown executable path");
                ShellVerb.Install(exe);
                Console.WriteLine($"added \"{ShellVerb.VerbText}\" to shortcuts and executables → {ShellVerb.Command(exe)}");
                return 0;
            case "uninstall":
                ShellVerb.Uninstall();
                Console.WriteLine($"removed \"{ShellVerb.VerbText}\"");
                return 0;
            default:
                throw new UsageException("shell install|uninstall");
        }
    }

    private static int Index(string s) =>
        int.TryParse(s, out var i) && i >= 0 ? i : throw new UsageException($"'{s}' is not an index ≥ 0");

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void Table(string[] header, IReadOnlyList<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Min(60, rows.Select(r => r[i].Length).Append(h.Length).Max())).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => (c.Length > widths[i] ? c[..(widths[i] - 1)] + "…" : c).PadRight(widths[i]))).TrimEnd();
        Console.WriteLine(Line(header));
        foreach (var row in rows)
            Console.WriteLine(Line(row));
    }

    /// <summary>From the Explorer verb the console window closes at once, so results go to a message box instead.</summary>
    private static void Report(bool notify, string message, bool error)
    {
        if (notify)
            MessageBoxW(IntPtr.Zero, message, "Managed App Launcher", error ? 0x10u : 0x40u);
        else
            (error ? Console.Error : Console.Out).WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
