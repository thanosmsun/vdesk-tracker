# Task View Management — Extension to PLAN.md

This document extends the original `PLAN.md` with the **Task View management** layer. The original plan implemented passive time tracking per desktop/window. This extension adds an active management layer: the user can see all Windows virtual desktops, create new ones, pick which to track, and assign programs that auto-launch with a desktop.

## Motivation

The user's actual workflow needs:
- **Track time per task view and per window** (already implemented, kept as-is)
- **Select which task views are visible/active** in the app
- **Create new task views** directly from the app
- **Assign programs to a task view** that auto-launch when the app discovers the desktop

The original PLAN.md is a pure background tracker. This extension makes the app a *task view manager* in addition to a tracker.

## Scope decisions (locked in)

| Question | Answer |
|---|---|
| Auto-launch trigger | On **app startup** AND on every **switch** (Win+Tab, our Switch button, `VirtualDesktop.CurrentChanged` event). See §10.7 for the single-instance vs multi-instance dispatch rules. |
| Disabled task views | Time tracking is **paused** for them. Foreground events on a disabled desktop are not written to `time_entries`. |
| "Visible" / "tracked" | Synonym: a task view is tracked iff `is_enabled = 1`. The report and main window only show enabled ones. |
| Windows virtual desktop lifecycle | We use the third-party `VirtualDesktop` NuGet package (Grabacr07) to wrap the undocumented COM `IVirtualDesktop*` API. Pure-ComImport is feasible but adds a lot of code that already exists upstream. |

## 1. Schema (additive, v1 → v2)

```sql
-- New table, v2
CREATE TABLE IF NOT EXISTS task_view_config (
    desktop_id        TEXT PRIMARY KEY,            -- Windows virtual desktop GUID
    display_name      TEXT NOT NULL,               -- user-visible label (may differ from Windows name)
    is_enabled        INTEGER NOT NULL DEFAULT 1,  -- 1 = tracked, 0 = ignored
    auto_launch       TEXT NOT NULL DEFAULT '[]',  -- JSON array of program paths
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);
```

**Migration**: in `DataStore.EnsureSchema`, if `PRAGMA user_version < 2`, run `CREATE TABLE task_view_config ...` then `PRAGMA user_version = 2`. Bump existing `user_version` in the same transaction.

**Important**: `task_view_config` is keyed by `desktop_id` (the Windows virtual desktop GUID), not by a synthesized ID. The GUID is stable for the lifetime of a desktop on a given Windows session; Windows may re-use GUIDs across reboots — this is acceptable because we re-populate on startup by enumerating all current Windows desktops.

## 2. New component: `TaskViewManager`

Responsibilities:
- Enumerate all current Windows virtual desktops via `VirtualDesktop` library
- Create a new virtual desktop
- Switch to a virtual desktop
- Persist per-desktop config (display name, is_enabled, auto_launch list) to `task_view_config`
- On **app startup**: for each Windows desktop, look up its config. If `is_enabled=1` and `auto_launch` is non-empty, run `Process.Start` for each program path (skip if already running, log skip).
- On **app startup** AND on every **switch** to a desktop, dispatch each program path in `auto_launch`: if the program is in the `SingleInstanceApps` list and already running, move its main window to the new desktop (reflection on `VirtualDesktopManagerInternal.MoveViewToDesktop`); otherwise launch a fresh instance. See §10.7 for the full rule.
- Raise events when desktops are created / removed (Windows may create or destroy desktops; we re-sync to the DB on such events).

API surface (high-level, internal to the app):
```csharp
public sealed class TaskViewManager : IDisposable
{
    public IReadOnlyList<TaskViewInfo> Enumerate();        // list Windows desktops
    public TaskViewInfo Create(string? displayName);       // create a new desktop, persist config row
    public void Remove(Guid desktopId);                    // not all Windows versions support remove
    public void SwitchTo(Guid desktopId);                  // make a desktop current
    public void ApplyConfig(Guid desktopId, bool isEnabled, string[] autoLaunchPaths);
    public void RunStartupAutoLaunch();                    // called once at app start
    public event Action<TaskViewInfo>? DesktopCreated;
    public event Action<Guid>? DesktopRemoved;
    public event Action? DesktopsChanged;
}
public sealed record TaskViewInfo(Guid DesktopId, string DisplayName, bool IsEnabled, string[] AutoLaunch);
```

**Config persistence rule**: when a Windows desktop is found for the first time, insert a `task_view_config` row with `is_enabled=1` and empty `auto_launch`. The user can later edit it via `TaskViewWindow`. Desktops that are removed (Windows destruction) are not auto-deleted from `task_view_config` — they remain as historical records; the row's desktop_id becomes orphan until reused or cleaned manually.

**Auto-launch behavior on startup** (matches user's answer):
1. After DB init and `CrashRecovery.Run()`, call `TaskViewManager.RunStartupAutoLaunch()`.
2. For each Windows desktop in the list, look up config:
   - If `is_enabled=1` and `auto_launch` has paths, for each path:
     - Resolve absolute path; if it doesn't exist, log warning and skip
     - If a process with that image name is already running, log info and skip
     - Otherwise `Process.Start(path)` (shell-execute, so URL/PDF/anything works)
3. Auto-launch runs on a worker thread (or directly) but is awaited before `Application.Run`.

## 3. New component: `TaskViewWindow` (WinForms)

Modeless form, opened from the Main window's "Task Views" button.

Layout:
```
+--------------------------------------------------------+
| VDesk Tracker - Task Views                  [_][X]     |
+--------------------------------------------------------+
| [Create New Task View]   [Refresh]   [Keep on Top [ ]] |
+--------------------------------------------------------+
| Name           | Enabled | Auto-launch programs       |
|----------------|---------|-----------------------------|
| Work           | [x]     | [Edit...] 3 programs       |
| Personal       | [x]     | [Edit...] 0 programs       |
| Archive        | [ ]     | [Edit...] 1 program        |
| ...                                                  |
+--------------------------------------------------------+
| [Switch to selected]   [Remove]                        |
+--------------------------------------------------------+
```

Behaviors:
- Double-click a row → open the **TaskViewEditDialog** (modal) showing:
  - Display name (TextBox)
  - Auto-launch programs (ListBox + Add/Remove buttons + Open File dialog)
- Clicking "Create New Task View":
  - Calls `TaskViewManager.Create(displayName)`
  - Refreshes the grid
  - Optionally switches to the newly created desktop
- Checkbox in the grid: toggles `is_enabled` and immediately calls `ApplyConfig`
- "Switch to selected" button: calls `TaskViewManager.SwitchTo(desktopId)` — Windows focuses the chosen virtual desktop
- "Remove" button: best-effort remove of the Windows virtual desktop (not always supported)
- "Refresh" button: re-enumerates Windows desktops, syncs the grid

## 4. Changes to existing components

### 4a. `SessionManager.OnForegroundChanged`

Before writing a time entry, check if the new `desktopId` is enabled in `task_view_config`:
- If the desktop is unknown (not in DB): treat as enabled (back-compat — it just got discovered).
- If the desktop is known and `is_enabled=0`: skip the time entry, do not update `LastNonNullDesktopId`, do not update `CurrentDesktopId`. The next foreground change to an enabled desktop will resume normal tracking.

This requires `SessionManager` to receive a callback `Func<Guid, bool> isDesktopEnabled`. It is constructed with that callback in `Program.cs`.

### 4b. `MainWindow`

Add a button "Task Views..." next to "Show Report...". On click, opens (or focuses) `TaskViewWindow`.

The current-desktop display label should indicate when the current desktop is disabled, e.g. "Personal (paused)" or grayed out.

### 4c. `ReportWindow`

Add a "Visible task views" multi-select dropdown (CheckedListBox) at the top:
- Default: all known Windows desktops checked.
- Disabled task views do not appear in the dropdown (they have no time entries to show).
- When a checkbox is unchecked, the corresponding desktop is excluded from the generated report.
- An "All" button selects all.

### 4d. `DataStore`

Add migration v1→v2 (CREATE TABLE `task_view_config`).
Add CRUD methods:
```csharp
IReadOnlyList<TaskViewConfigRow> GetAllTaskViewConfigs();
TaskViewConfigRow? GetTaskViewConfig(Guid desktopId);
void UpsertTaskViewConfig(Guid desktopId, string displayName, bool isEnabled, string[] autoLaunch);
bool IsDesktopEnabled(Guid desktopId);   // returns true if unknown or enabled
```
And a way to seed config rows on startup:
```csharp
void EnsureTaskViewConfigRow(Guid desktopId, string defaultName);
```

### 4e. `Program.cs`

- Add `TaskViewManager taskViews = new(...)` after DataStore init
- Call `taskViews.EnumerateAndSeed()` (creates config rows for any unknown Windows desktop)
- Call `taskViews.RunStartupAutoLaunch()` (one-time launch of assigned programs for enabled desktops)
- Wire up `TaskViewWindow` and pass it to `MainWindow`
- Pass `taskViews.IsDesktopEnabled` to `SessionManager`

## 5. Schema migration v1→v2 details

In `DataStore.EnsureSchema`:
```csharp
if (currentVersion < 2)
{
    using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS task_view_config (
                desktop_id   TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                is_enabled   INTEGER NOT NULL DEFAULT 1,
                auto_launch  TEXT NOT NULL DEFAULT '[]',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }
    SetUserVersion(c, 2);
}
```

## 6. Auto-launch semantics — careful notes

- **When**: at app startup AND on every switch. See §10.7 for the dispatch rules.
- **Already-running check**: only used for apps in the `SingleInstanceApps` set. Image name match via `Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))`. Multi-instance apps (Chrome, Firefox, VS Code, …) skip this check and always launch fresh.
- **Failure modes**: if path doesn't exist, log warning and skip. If `Process.Start` throws, log warning and continue. We never block startup on a single failed program.
- **Reflection on `MoveViewToDesktop`**: see §10.7 for the path. Sticky-fail on miss is a future improvement.

## 7. New UAT scripts

Add to PLAN.md §9:

### UAT-15: Task View Manager — Create
- Open Main window → click "Task Views..."
- Click "Create New Task View", enter name "QA"
- Verify: row "QA" appears in the grid
- Verify: Windows Task View (Win+Tab) shows a new desktop called "QA"

### UAT-16: Task View Manager — Disable
- Start tracking
- In Task View window, uncheck the "Enabled" checkbox on the current desktop
- Switch to that desktop, open an app, wait 30s
- Open Report, generate "Today"
- Verify: no time is attributed to that desktop

### UAT-17: Auto-launch on Startup
- In Task View window, edit "QA", add `notepad.exe` to auto-launch
- Exit the app
- Start the app again
- Verify: Notepad opens automatically

### UAT-18: Report Filter
- Generate "Today" report
- Open the "Visible task views" dropdown
- Uncheck one desktop
- Verify: report regenerates excluding that desktop
- Verify: "All" button re-enables all

## 8. Updated UAT scripts for the original plan

The original §9 UAT scripts stay valid except:
- **UAT-1**: tray icon → Main window. Balloon → log info.
- **UAT-2**: tray menu → Main window buttons.
- **UAT-9**: rename desktop → "Rename" via the Task View window's row edit (double-click).
- **UAT-14**: elevated exit policy changed to **log warning + continue** (decided earlier).

## 9. Decisions confirmed (2026-06-01)

1. **Switch to selected** — yes, actually switches the active Windows virtual desktop. Implemented via `VirtualDesktop.this[id].Switch()`.
2. **Remove button** — exposed, with best-effort behavior. Windows's `IVirtualDesktop::Remove()` succeeds only for the right-most desktop; for others we report `"Only the right-most desktop can be removed"` and leave the row in place.
3. **Default display name** — use the existing "Desktop N" auto-naming (matches the `AutoDesktopName` logic in `SessionManager`).
4. **Auto-launch timing** — superseded by §10.7: auto-launch now fires on switch too. Programs added to a desktop's auto-launch list **after** the app has been running will launch on the **next switch** to that desktop (no app restart needed).

## 10. Implementation notes (2026-06-02)

What changed during implementation, beyond what §1–9 specified.

### 10.1 Renamer: bypass the public API

The public `IVirtualDesktopManager` (`CLSID_AA509086-5CA9-4C25-8F95-589D3C07B48A`, `twinapi.dll`) is still registered on Windows 11 24H2 build 26100, but its `GetWindowDesktopId` and `MoveWindowToDesktop` methods return `E_NOINTERFACE` from the Windows runtime (the public IID no longer matches the coclass's vtable). The Slions `VirtualDesktop` library works around this by using **internal** Slions-specific IIDs and CLSIDs, but exposes only a read-only `Name` property.

To set a desktop's Windows-side name, the implementation reflects into the Slions provider to reach the **internal** `VirtualDesktopManagerInternal` and calls its `SetDesktopName(IVirtualDesktop, string)` method. Path:

```
typeof(VirtualDesktop)
   .GetField("_provider", Static | NonPublic)
      → provider instance
provider.GetType()
   .GetProperty("VirtualDesktopManagerInternal", Instance | Public | NonPublic)
      → manager instance
manager.GetType()
   .GetMethod("SetDesktopName", Instance | Public | NonPublic)
      → setName(sourceDesktop, newName)
```

All `MethodInfo`s and field values are cached under a double-check lock. The first reflection miss flips a sticky `_failed` flag so subsequent calls short-circuit to `false` instead of paying the reflection cost. The renamer is public (`VirtualDesktopTracker.Components.VirtualDesktopRenamer`) for testability and is consumed by `TaskViewManager.TrySetWindowsName`.

**Trade-off**: this depends on Slions package internals. A major Slions version bump may rename `_provider` / `VirtualDesktopManagerInternal` / `SetDesktopName`; the sticky-fail flag will then disable renaming silently (logged at warning level) instead of crashing. Recovery is a one-line reflection-path fix.

### 10.2 HWND → DesktopId: Slions `IApplicationView`

Same root cause as the renamer: `IVirtualDesktopManager.GetWindowDesktopId` returns `E_NOINTERFACE` on 24H2. The listener's `ResolveDesktopId` now prefers a Slions path:

```
typeof(VirtualDesktop).GetField("_provider", Static|NonPublic)
   → provider
provider.GetType().GetProperty("ApplicationViewCollection", Instance|Public|NonPublic)
   → ApplicationViewCollection
av.GetType().GetMethod("GetViewForHwnd", Instance|Public|NonPublic)
   → IApplicationView (RCW)
Type.GetType("WindowsDesktop.Interop.Proxy.IApplicationView, VirtualDesktop")
   .GetMethod("GetVirtualDesktopId", Instance|Public|NonPublic)
   → Guid
```

The `IApplicationView` interface type is referenced by name+assembly so we don't have to load it by namespace. The implementation is in `WinEventHookListener.DesktopResolver`. The per-call `IApplicationView` RCW is `Marshal.ReleaseComObject`-released to avoid a leak under high event rates.

**Lazy init quirk**: the first access to `ApplicationViewCollection` on a fresh process throws `InvalidOperationException("Initialization is required.")`. Subsequent calls succeed. `DesktopResolver.TryCreate` therefore calls the getter up to twice: first to trigger initialization, then to fetch the real value. Failures are logged at info/warning and the listener falls back to the legacy `VirtualDesktop.Current.Id` path (which is sufficient for foreground-window resolution, since the listener is only invoked on foreground changes and the foreground hwnd is on the current desktop).

### 10.3 `AppPickerDialog` (replaces `OpenFileDialog`)

The original `TaskViewEditDialog` used `OpenFileDialog` to add a program, which on Windows defaults the `InitialDirectory` to `System32`. Users had to navigate manually to Start Menu or `Program Files`.

`AppPickerDialog` (`Components/AppPickerDialog.cs`) is a modeless-feeling modal WinForms dialog with:
- A `ListView` (Details) with `Name` / `Publisher` / `Path` columns.
- 16×16 icon thumbnails extracted from the target `.exe` via `Icon.ExtractAssociatedIcon`.
- A `TextBox` for live substring search across all three columns.
- An `OK` button (enabled only when a row is selected) and `Cancel`.
- A "Browse..." button that opens an `OpenFileDialog` (with `InitialDirectory = Environment.SpecialFolder.ProgramFiles) as a manual fallback.

Two sources are enumerated:
1. **Start Menu shortcuts** under `%APPDATA%\Microsoft\Windows\StartMenu\Programs\**\*.lnk` and `%PROGRAMDATA%\Microsoft\Windows\StartMenu\Programs\**\*.lnk`. Targets are resolved via `WScript.Shell` COM (`Type.GetTypeFromProgID("WScript.Shell").CreateInstance()` → `CreateShortcut(path)` → `TargetPath`). `DirectoryName` is used as a fallback when `TargetPath` is empty. `IWshShortcut` instances are `Marshal.ReleaseComObject`-d in a `finally`.
2. **Registry-installed apps** under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*` and the `WOW6432Node` mirror, plus `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*`. Each sub-key's `DisplayName` and `DisplayIcon` (or `InstallLocation` + guessed executable) are read; entries are skipped when no resolvable `.exe` exists.

Results are deduplicated by resolved path (case-insensitive). The dialog is invoked from `TaskViewWindow.OnAdd` and returns the chosen path to the edit dialog's program list.

### 10.4 "Rename Desktop" → "Rename Task View"

The Main window's button was renamed and rewired:
- **Before**: `Rename Desktop...` button, gated on `IsTracking`, called `renamer.TrySetName(currentDesktop, newName)` directly.
- **After**: `Rename Task View...` button, **not** gated on tracking, opens the `TaskViewEditDialog` for the current desktop's `TaskViewInfo` and on OK calls `TaskViewManager.ApplyConfig(desktopId, isEnabled, autoLaunch)` — same path as editing any row in the Task Views window.

`RenameCurrentTaskView` lives in `Program.cs` and looks up the current desktop's `TaskViewInfo` via `taskViews.Enumerate().FirstOrDefault(t => t.DesktopId == current.Id)`. If the user is on a desktop that has no `task_view_config` row (e.g. just created Windows-side), one is seeded on demand before editing.

### 10.5 Report empty-state overlay

`ReportWindow` now hosts a centered `Label` (`_emptyStateLabel`) that is shown when the generated query returns 0 rows. The label shows "No time entries in the selected range", the active range, and a hint to start tracking. It is hidden when results exist or when the user changes the date range / filters.

### 10.6 Plan subagent

A new `plan` subagent has been registered at `~/.config/opencode/agents/plan.md` (model: `opencode/qwen3.6-plus-free`). It is not loaded in the current opencode session — a restart of opencode is required for it to appear in the agent picker.

### 10.7 Auto-launch: try-then-fallback strategy (2026-06-02)

User correction after the first build: hardcoding a list of single-instance apps is brittle. The dispatcher should **try to launch the program first**, and only fall back to moving an existing instance if the launch fails or the launched process exits immediately.

**Decision** (§9 item 4 is superseded; the inline `SingleInstanceApps` HashSet was removed):
- Auto-launch fires on **app startup** AND on every **switch** (Win+Tab, our Switch button, `VirtualDesktop.CurrentChanged` event).
- Per program path, `LaunchOrMoveOne` does:
  1. Validate the path / file exists.
  2. `Process.Start` with `UseShellExecute = true`.
  3. If `Process.Start` **throws** (rare — file association errors, UAC, etc.): log + fall back to `TryMoveExistingToDesktop`.
  4. If the new process **exits within 350 ms** (`started.HasExited == true`): this is the single-instance-mutex pattern (Spotify, Teams, the tracker itself, …). Dispose the dead process, log, fall back to `TryMoveExistingToDesktop`.
  5. Otherwise the new instance is alive — leave it running. This is the multi-instance path (Chrome, Firefox, VS Code, Notepad, …).
- `TryMoveExistingToDesktop(imageName, target, targetName)`:
  1. `Process.GetProcessesByName(imageName)` to find the already-running instance(s).
  2. For each process with a `MainWindowHandle != IntPtr.Zero`, reflect on `VirtualDesktopManagerInternal.MoveViewToDesktop(IntPtr, IVirtualDesktop)` to attach it to the target desktop.
  3. Log a per-window outcome.
- No hardcoded list of single-instance apps. No per-program "single instance" toggle in the auto-launch data model (`auto_launch` is still a plain `string[]` of paths in `task_view_config`).
- The 350 ms wait is `Thread.Sleep` on the caller's thread. For an interactive Switch click on the UI thread this is acceptable (sub-second). A future improvement could move the wait onto a background timer and use `HasExited` polling without blocking, but it's not currently a hot path.

**Reflection path** for `MoveViewToDesktop` (verified end-to-end with Notepad):
```
typeof(VirtualDesktop).GetField("_provider", Static|NonPublic)
   → provider
provider.GetType().GetProperty("VirtualDesktopManagerInternal", ...)
   → manager
manager.GetType().GetMethod("MoveViewToDesktop", ...)
   → bool MoveViewToDesktop(IntPtr hWnd, IVirtualDesktop desktop)
```
Note the method name is `MoveViewToDesktop` (not `MoveWindowToDesktop`). It returns `bool` and is part of the internal `IVirtualDesktopManagerInternal` interface (IID `53F5CA0B-158F-4124-900C-057158060B27`).

### 10.8 Close windows when removing a task view (2026-06-02)

User request: removing a task view should also close the programs running on it. By default Windows reassigns orphaned windows to the previous desktop, which is not what the user wants.

**Decision**: `TaskViewManager.Remove(Guid desktopId, bool closeWindows = true)` enumerates all top-level windows on the target desktop via `EnumWindows`, sends `WM_CLOSE` (0x0010) to each, waits 500 ms for them to process, then calls `desktop.Remove()`.

**Implementation details**:
- `CloseWindowsOnDesktop(Guid desktopId)`:
  1. Iterates all `EnumWindows` callbacks.
  2. Skips invisible windows (`IsWindowVisible == false`).
  3. Skips owned windows (`GetWindow(hWnd, GW_OWNER) != 0` — these are tooltips, menus, message-only windows).
  4. Skips the tracker's own process (`pid == Environment.ProcessId`) — we cannot close ourselves.
  5. Uses a `Func<IntPtr, Guid?>` resolver (plumbed from `WinEventHookListener.DesktopIdResolver` via `TaskViewManager.SetWindowDesktopResolver`) to check whether the window is on the target desktop. Non-matching windows are skipped.
  6. For matching windows, captures the title via `GetWindowText` for logging, then `PostMessageW(hWnd, WM_CLOSE, 0, 0)`.
  7. Returns the number of windows to which WM_CLOSE was sent.
- The 500 ms wait between sending WM_CLOSE and calling `desktop.Remove()` gives well-behaved apps a chance to flush + exit. Apps that ignore WM_CLOSE (or take longer to close) will still get their windows orphaned to another desktop on `desktop.Remove()` — that's a Windows constraint, not ours.
- The Remove confirmation dialog in `TaskViewWindow.OnRemoveClicked` now reads "Remove task view 'X' and close all programs on it?" so the user knows what will happen.
- New P/Invokes added to `NativeMethods.cs`: `IsWindowVisible`, `GetWindow`, `EnumWindows` (+ `EnumWindowsProc` delegate), `GetWindowTextRaw`, `GetWindowTextLengthW` (+ `GetWindowText` helper). Constants: `GW_OWNER = 4`, `WM_CLOSE = 0x0010`.
