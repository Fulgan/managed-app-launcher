# Managed App Launcher

Stream Deck plugin for Windows that launches applications from managed lists. Includes a list manager companion app for designing lists. The Stream Deck plugin itself mostly only uses these lists (although it includes a launcher button that can work independently)

## Features

- Named lists of applications, edited in a desktop manager and shown live on any deck.
- Automatic paging with page dots, plus row and page breaks.
- A press on a running application brings it to the front; a long press closes it.
- Shortcuts, programs, versioned folders such as `app-*\Discord.exe`, Store apps and Steam games.

| Dot | State |
|---|---|
| yellow | starting |
| green | running |
| red | running as administrator |

(Applications running as administrator cannot be closed).

## Requirements

| Component | Requirement |
|---|---|
| Windows | 10 or later |
| Stream Deck software | 6.4 or later |
| Devices | Stream Deck, Stream Deck XL, Stream Deck Mini, Stream Deck +, Stream Deck Neo |

## Installation

Opening `com.fulgan.managedapplauncher.streamDeckPlugin` from the release page installs the plugin. The first use
of a plugin action on a deck offers to install the launcher profile.

## Actions

| Action | Function |
|---|---|
| **Open Launcher** | Shows the chosen list on this deck, with a Back key to the previous profile |
| **Manage Apps** | Opens the manager |
| **App Slot** | Key of the launcher profile |
| **Launch App** | Launches one application configured on the key |

## Manager

- Adds applications from the Start menu, running applications, Steam, files or drag and drop.
- Drag and drop reorders a list; dropping on another list moves the item, Ctrl copies it.
- Each deck can be dedicated to one list.
- The preview icon is a test key: a click is a short press, holding it is a long press.

The configuration is `%APPDATA%\ManagedAppLauncher\config.json`. `malaunch.exe` in the plugin folder edits it from
the command line; `malaunch` without arguments lists the commands.

## Build

.NET 10 SDK and PowerShell 7.

```powershell
./build.ps1             # package in dist\
./build.ps1 -Install    # also installs into Stream Deck and restarts it
dotnet test ManagedLauncher.slnx
```

## License

[MIT](LICENSE)
