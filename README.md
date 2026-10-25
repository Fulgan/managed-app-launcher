# Managed App Launcher

An app launcher for [Elgato Stream Deck](https://www.elgato.com/stream-deck) on Windows, driven by lists you
manage instead of keys you configure one by one. Pin apps to a list once; the launcher lays them out across
your deck, pages through them when they don't fit, and shows which ones are running.

- **Lists, not keys.** Keep several app lists ("Work", "Games", …). A launcher key shows any of them; adding or
  reordering apps updates the deck immediately.
- **Automatic paging.** Lists longer than the deck get a next-page key with page dots. The layout adapts to
  Stream Deck, Stream Deck XL and Stream Deck Mini.
- **Running indicator.** A green dot marks apps that are running. Pressing a running app brings its window to
  the front instead of starting a second copy.
- **Long press.** Closes a running app (gracefully, then forcibly), or starts a stopped one as administrator.
- **Survives updates.** Targets can be shortcuts, Store apps (AUMID) or versioned folders such as
  `%LOCALAPPDATA%\Discord\app-*\Discord.exe`, resolved at every press, so side-by-side updates keep working.
- **Manager app.** A desktop window to edit lists: add apps from the Start menu, from running apps, by file or
  by drag and drop, reorder, rename, and set per-app behaviour.

## Requirements

- Windows 10 or later
- Stream Deck software 6.4 or later

## Install

Build the plugin (see below) and double-click `dist\com.fulgan.managedapplauncher.streamDeckPlugin`. Stream
Deck installs it and offers the launcher profile the first time you use one of its actions on a deck.

## Use

The plugin adds four actions under **Managed App Launcher**:

| Action | What it does |
|---|---|
| **Open Launcher** | Switches the deck to the launcher showing the list chosen in its settings. The launcher gets a Back key (first key, first page) that returns to the profile you came from. |
| **Manage Apps** | Opens the manager window. |
| **App Slot** | The key type of the launcher profile; each one shows whatever app, page key or control falls on it. |
| **Launch App** | A single app configured on the key itself, outside any list. |

Typical setup: put one **Open Launcher** key per list on your usual profile. Press it to browse that list;
press Back to return.

In the manager you can also:

- **Dedicate a deck** to a list (Decks, bottom left): that deck always shows the launcher with that list.
- Turn on **Return after launch** for a list: after an app is launched or focused, the deck leaves the
  launcher (back to where you came from, or to the first page).
- Pick per-app behaviour through **presets**: what a press does while the app runs (focus, relaunch, nothing),
  what a long press does, which processes to close, admin, arguments and start folder.

Settings live in `%APPDATA%\ManagedAppLauncher\config.json`. The plugin picks up every change, from the manager,
the command line or a text editor, without a restart.

## Command line

`malaunch.exe` sits in the plugin folder and edits the same configuration:

```
malaunch pin <path|AUMID> [--list name] [--at n] [--preset name] [--title t]
malaunch unpin <index> [--list name]
malaunch move <from> <to> [--list name]
malaunch list [--list name]
malaunch lists | lists add <name> | lists rm <name> | lists rename <old> <new>
malaunch preset set <name> [--args a] [--workdir d] [--admin|--no-admin]
                           [--on-running auto|focus|relaunch|none] [--kill-mode all|newest]
                           [--indicator border|dot] [--long-press auto|none]
malaunch import <profile uuid|name> [--list name] [--append] [--dry-run]
malaunch shell install|uninstall
```

`import` builds a list from the launcher buttons of an existing Stream Deck profile. `shell install` adds a
"Pin to Stream Deck" entry to the Explorer context menu of programs and shortcuts.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell 7.

```powershell
./build.ps1            # dist\com.fulgan.managedapplauncher.sdPlugin and the .streamDeckPlugin package
./build.ps1 -Install   # also installs into Stream Deck, restarting it
dotnet test ManagedLauncher.slnx
```

| Path | Contents |
|---|---|
| `src/ManagedLauncher.Core` | Configuration, target resolution, process tracking, press handling, key rendering |
| `src/ManagedLauncher.Plugin` | The Stream Deck plugin (SDK v2 WebSocket client) |
| `src/ManagedLauncher.Manager` | The WPF manager app |
| `src/ManagedLauncher.Cli` | `malaunch.exe` |
| `plugin/` | Manifest, property inspectors and images |
| `tools/IconGen` | Generates every icon in `plugin/imgs` and the manager icon: `dotnet run --project tools/IconGen` |

## License

[MIT](LICENSE)
