# VirtualDesktopTracker

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
![License](https://img.shields.io/badge/license-All%20Rights%20Reserved-red)

A Windows tray application that tracks how much time you spend in each
application and window **per virtual desktop** — plus a built-in Task View
manager for renaming desktops and moving windows between them.

## Features

- **Per-window time tracking** across Windows virtual desktops, automatically
  following you when you switch desktops.
- **Report window** — review tracked time with desktop filtering and data export.
- **Task View management** — rename virtual desktops, launch applications on a
  specific desktop, and move existing windows between desktops.
- **Crash recovery** — tracking sessions survive unexpected restarts.
- **Single-instance guard** — launching a second instance exits cleanly.
- **Fully local** — all data stays on your machine. No network, no telemetry.

## How it works

VirtualDesktopTracker is a .NET 9 WinForms application that listens to Windows
virtual desktop changes (via the [Slions.VirtualDesktop](https://www.nuget.org/packages/Slions.VirtualDesktop)
library) and records window focus sessions into a local SQLite database
([Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite)),
with file logging via [Serilog](https://serilog.net).

## Requirements

- Windows 10 or 11 (x64)
- .NET 9 SDK — pinned by `global.json` (9.0.314, `rollForward: latestFeature`)

## Build & run

```pwsh
dotnet build
dotnet run --project VirtualDesktopTracker
```

## Publish (single-file executable)

```pwsh
dotnet publish VirtualDesktopTracker -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Produces a self-contained `VirtualDesktopTracker.exe` in `publish/`.

## Helper scripts (`publish/`)

| Script | Purpose |
| --- | --- |
| `CreateStartMenuShortcut.ps1` | Creates a Start Menu shortcut pointing at `publish/VirtualDesktopTracker.exe` |
| `GenerateAppIcon.ps1` | Regenerates `app.ico` / `app-256.png` in `VirtualDesktopTracker/Resources` |

Both scripts resolve paths relative to their own location, so they work from any clone.

## Data & logs

| Item | Location |
| --- | --- |
| Database | `%LOCALAPPDATA%\VirtualDesktopTracker\VirtualDesktopTracker.db` (SQLite, WAL) |
| Logs | `%LOCALAPPDATA%\VirtualDesktopTracker\logs\app-*.log` |

## License

**All Rights Reserved** — see [LICENSE](LICENSE).

You may view and fork this repository on GitHub, but any use, modification, or
redistribution of the code or the application requires the author's prior
written consent. Contact [@thanosmsun](https://github.com/thanosmsun) to
request permission.
