# Virtual Desktop Time Tracker — Final Design Plan

> **Status (2026-06-01)**: Original plan implemented for time tracking. A **Task View management** layer (create / enable / disable / assign auto-launch programs) is documented in [`PLAN-EXTENSION.md`](./PLAN-EXTENSION.md). This document is kept as the authoritative source for tracking behavior; the extension document is authoritative for the management layer.

## 1. High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                         VirtualDesktopTracker.exe                            │
│                        (.NET 9 WinForms, self-contained single-file)         │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Producers:        Channel:          Single-DB-Writer Actor:                 │
│  ┌──────────┐     ┌──────────┐     ┌──────────────────────────────┐         │
│  │WinEvent  │────▶│ Bounded  │────▶│ Actor thread                  │         │
│  │Hook      │     │ Channel  │     │                              │         │
│  ├──────────┤     │(1024,    │     │ Owns: SessionManager          │         │
│  │30s Timer │────▶│ Wait)    │────▶│       DataStore (writer conn) │         │
│  ├──────────┤     └──────────┘     │       TrayContext (reads)     │         │
│  │Power/Lock│────▶                 │                              │         │
│  │Handlers  │                      │ try/catch/log per event      │         │
│  ├──────────┤                      │ (rethrows OpCanceledException)│         │
│  │UI Message│────▶                 │ On terminal error:           │         │
│  │Pump      │                      │  → Red icon, balloon, unhook │         │
│  └──────────┘                      └──────────────────────────────┘         │
│                                                                              │
│  ┌───────────────────┐              ┌──────────────────────────┐           │
│  │ ReportWindow       │────reads───▶│ DataStore (reader conn)   │           │
│  │ (WinForm, modeless)│             │ (separate, WAL-compatible)│           │
│  └───────────────────┘             └──────────────────────────┘           │
│                                                                              │
│  Files: %LOCALAPPDATA%\VirtualDesktopTracker\                                │
│    VirtualDesktopTracker.db  [WAL journal mode; writer + reader conns]      │
│    VirtualDesktopTracker.db.bak-{1,2,3}                                      │
│    logs\app-{yyyyMMdd}.log                                                   │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Deployment**: .NET 9 WinForms, self-contained single-file publish. Native AOT deferred (COM interop complexity). If AOT pursued later, `IVirtualDesktopManager` needs source-generated `ComImport` wrappers.

**Channel**: Bounded(1024, Wait). Rationale: a slow actor manifests as missed foreground changes rather than unbounded memory. If field observation shows stalls, switch to `DropOldest` + metric.

---

## 2. Component Breakdown

### 2a. WinEventHookListener

- Registers `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` on Start, unregisters on Stop
- Callback receives `HWND` -> checks `_shuttingDown` flag
- For each event (lock-protected coalescing by `(desktopId, appPath)`):
  1. `IVirtualDesktopManager.GetWindowDesktopId(hwnd)` -> desktop GUID
  2. `GetWindowThreadProcessId(hwnd, out pid)` -> `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` -> `QueryFullProcessImageName` -> app path
  3. `GetAncestor(hwnd, GA_ROOTOWNER)`, skip `WS_EX_TOOLWINDOW` via `GetWindowLongPtr`
  4. If `desktopId == GUID_NULL` -> post `Guid.Empty`
  5. If non-null + unknown -> auto-name "Desktop N" into `desktop_names`
  6. Post `ForegroundChangedEvent` to channel
- Stop: set `_shuttingDown = true` -> `_callbackDone.Wait(500ms)` -> `UnhookWinEvent` -> drain channel
- `ResetCoalescing()` clears `_lastPostedEventKey` under lock

### 2b. Single-DB-Writer Actor

**Event types** (posted to channel):
- `ForegroundChanged(Guid DesktopId, string DesktopName, string AppPath, string AppName, DateTimeOffset Timestamp)`
- `CheckpointTimer(DateTimeOffset)`
- `Suspend()` / `Resume(DateTimeOffset)` / `SessionLock()` / `SessionUnlock(DateTimeOffset)`
- `EndSessionRequest(DateTimeOffset)`
- `StartTracking()` / `StopTracking()`
- `ResetCoalescing()`

**Actor loop**:
```csharp
while (!cancellationToken.IsCancellationRequested) {
    var evt = await channel.Reader.ReadAsync(ct);
    if (state == Suspended && evt is not Resume && evt is not Suspend) continue;
    try {
        ProcessEvent(evt);
    } catch (OperationCanceledException) {
        throw;
    } catch (SqliteException sqlex) when (IsFullDisk(sqlex)) {
        Log.Error(...); SetIcon(Red); ShowBalloon(...); StopHook();
    } catch (Exception ex) {
        Log.Error(ex, ...); SetIcon(Red); ShowBalloon(...);
    }
}
```

`ResetCoalescing` calls `listener.ResetCoalescing()` via held reference.

### 2c. SessionManager (state machine)

**State**:
- `IsTracking: bool`
- `ActorState: enum { Active, Suspended }`
- `CurrentSessionId, CurrentDesktopId, CurrentDesktopName`
- `CurrentAppPath, CurrentAppName`
- `IntervalStartedAtTick: long (TickCount64)` - monotonic elapsed
- `IntervalStartedAtWall: DateTimeOffset` - human anchor
- `LastNonNullDesktopId, LastNonNullDesktopName` - GUID_NULL carry-forward
- `LastTimeEntry` - for crash recovery

**OnStartTracking()**:
- Register WinEventHook, post ResetCoalescing
- Reset all timing state: `IntervalStartedAtTick = TickCount64; IntervalStartedAtWall = DateTimeOffset.UtcNow; LastNonNullDesktopId = Guid.Empty; LastNonNullDesktopName = ""; CurrentDesktopId = Guid.Empty; CurrentDesktopName = ""; CurrentAppPath = ""; CurrentAppName = ""; LastTimeEntry = default`
- `INSERT INTO sessions (start_reason='user_start')`, start 30s timer, flip flags

**OnForegroundChanged(desktopId, appPath, appName)**:
- If not tracking -> return
- `GUID_NULL`: carry forward `LastNonNullDesktopId` if set; if both empty -> skip
- Non-null: update `LastNonNullDesktopId`
- `elapsedMs = TickCount64 - IntervalStartedAtTick`
- If elapsed > 0: `INSERT time_entry (..., checkpoint=false)`
- Update state, reset timers, update `LastTimeEntry`

**OnCheckpointTimer()**:
- If not tracking or `CurrentDesktopId == Guid.Empty` -> return
- `elapsedMs = TickCount64 - IntervalStartedAtTick`
- If > 0: `INSERT time_entry (checkpoint=true)`, reset timers

**OnSuspend/SessionLock()**:
- If `ActorState == Suspended` -> return (duplicate guard)
- Flush checkpoint, `ActorState = Suspended`, `listener.ResetCoalescing()`

**OnResume/SessionUnlock()**:
- `ActorState = Active`
- Reset timers, clear `LastNonNullDesktopId`, `listener.ResetCoalescing()`

**OnCrashRecovery() - runs at startup after DB init, before tray icon**:
- Find orphaned session (`ended_at IS NULL`)
- Get last `time_entry` -> `(desktopId, desktopName, appPath, appName, startedAtTick, durationMs)`
- `gapMs = TickCount64 - (startedAtTick + durationMs)`; if < 0 -> 0
- Create new session (`start_reason='crash_recovery'`)
- `INSERT` recovery `time_entry` with full gap, `is_recovery=1`, using last entry's desktop/app
- `UPDATE sessions SET ended_at = @now` on orphaned session
- Balloon: "Recovered Xs from previous session"

### 2d. DataStore

**Initialize()**:
- Create directory, rotate backups (3 copies)
- Open writer connection: `PRAGMA journal_mode=WAL`, `PRAGMA foreign_keys=ON`
- `PRAGMA integrity_check` -> on failure restore from `.bak-1`, balloon
- Run migrations via `PRAGMA user_version`
- Insert default settings

**Writer methods**: `InsertTimeEntry`, `CreateSession`, `EndSession`, `UpsertDesktopName`, `UpsertSetting`, `StampEndedAtOnOrphanedSession`

**Reader connection**: Separate read-only conn. Opened in `ReportWindow.Load`, closed in `FormClosed`.

### 2e. TrayContext

**Status (2026-06-01)**: replaced by a real GUI window (`MainWindow`). The app is a normal windowed app that can be pinned to the taskbar. Closing the window exits the app cleanly. No tray icon, no minimize-to-tray, no balloons.

The window layout:
- Status strip at top: green/red status dot + "Tracking" / "Stopped" label
- Current desktop / app / elapsed labels
- Buttons: Start Tracking, Stop Tracking, Show Report..., Rename Desktop..., Task Views..., Launch at Startup (checkbox), Exit

**Manifest**: `asInvoker` + `dpiAwareness=PerMonitorV2`. Elevated -> log warning and continue (no exit).

**Target**: Windows 10 21H2+, Windows 11.

**Single-instance**: Named `Mutex` (`Local\VDeskTracker-{user}`). Second instance exits.

### 2f. ReportWindow

Modeless WinForm:
```
+-------------------------------------------------+
| [VDesk Tracker - Report]                  [_][X] |
+-------------------------------------------------+
| ( ) Today  ( ) 7 Days  ( ) 30 Days  ( ) Custom |
| From: [date]  To: [date]                        |
| [Generate Report]                               |
+-------------------------------------------------+
| Desktop       | App        |     Time |  %      |
| Work          |            |   4h 30m |  60%    |
|               | chrome.exe |   2h 15m |  30%    |
|               | slack.exe  |   1h 05m |  14%    |
|               | vscode.exe |   1h 10m |  16%    |
| Personal      |            |   3h 00m |  40%    |
|               | spotify.exe|   1h 30m |  20%    |
|               | firefox.exe|   1h 30m |  20%    |
| TOTAL         |            |   7h 30m | 100%    |
+-------------------------------------------------+
| [ ] Include recovery entries   [ ] Keep on Top  |
| [Export CSV...]                                 |
+-------------------------------------------------+
```

- Preset date buttons, custom date pickers
- Grouped grid: bold desktop headers, indented apps, subtotals + percentages
- Recovery entries toggle (checkbox, default on)
- Double-click desktop -> rename dialog (local label only)
- Export CSV

---

## 3. SQLite Schema

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS sessions (
    id            TEXT PRIMARY KEY,
    started_at    TEXT NOT NULL,           -- ISO 8601 UTC
    ended_at      TEXT,                    -- NULL = active or crashed
    start_reason  TEXT NOT NULL DEFAULT 'startup' CHECK(start_reason IN (
                      'startup','user_start','resume','crash_recovery')),
    created_app   TEXT NOT NULL,
    machine_name  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS time_entries (
    id            INTEGER PRIMARY KEY,
    session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    desktop_id    TEXT NOT NULL,
    desktop_name  TEXT NOT NULL,            -- denormalized, historical name at entry time
    app_path      TEXT NOT NULL DEFAULT '',
    app_name      TEXT NOT NULL,
    started_at    TEXT NOT NULL,            -- ISO 8601 UTC
    duration_ms   INTEGER NOT NULL CHECK(duration_ms >= 0),
    is_checkpoint INTEGER NOT NULL DEFAULT 0 CHECK(is_checkpoint IN (0,1)),
    is_recovery   INTEGER NOT NULL DEFAULT 0 CHECK(is_recovery IN (0,1))
);
CREATE INDEX idx_te_started ON time_entries(started_at);
CREATE INDEX idx_te_session ON time_entries(session_id);

CREATE TABLE IF NOT EXISTS desktop_names (
    desktop_id    TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS meta (
    key           TEXT PRIMARY KEY,
    value         TEXT NOT NULL
);
```

**Migrations**: `PRAGMA user_version` on startup. v0->v1: create tables. Future: sequential version steps.

---

## 4. Data Flow

### Startup sequence
1. Single-instance Mutex check -> second instance exits
2. Data directory + backup rotation (3 copies)
3. DB open with WAL + FKs + integrity_check -> restore from .bak if corrupt
4. **`OnCrashRecovery()`** - find orphan, compute gap, insert recovery entry, stamp ended_at, balloon
5. Schema migrations
6. Tray icon shown (Stopped state, gray)
7. Power/lock/endsession handlers registered
8. User clicks Start to begin

### Normal tracking (after Start)
- WinEventHook fires on every foreground change -> callback checks coalescing key `(desktopId, appPath)` -> if same, skip; if different, post event
- Actor computes `TickCount64 - IntervalStartedAtTick`, writes `time_entry` with precise duration, resets interval
- 30s timer posts checkpoint -> actor writes `time_entry (checkpoint=1)` with the interval elapsed

### GUID_NULL handling
- If `desktopId == Guid.Empty`: carry forward `LastNonNullDesktopId`. If both empty -> skip event entirely
- Only non-null GUID transitions create `desktop_names` rows
- `LastNonNullDesktopId` is cleared on resume (after sleep/lock)

### Sleep / Connected Standby / Lock
- S3 sleep: `PowerModeChanged` -> Suspend
- Connected Standby: `RegisterSuspendResumeNotification` -> Suspend/Resume
- Lock: `WTS_SESSION_LOCK` -> SessionLock
- Duplicate guard: `if ActorState == Suspended -> return`
- Suspend: flush checkpoint, `ActorState = Suspended`, ResetCoalescing
- Resume: `ActorState = Active`, reset interval timers, clear `LastNonNullDesktopId`, ResetCoalescing
- **Gap is not attributed. No phantom time.**

### Shutdown
- `WM_QUERYENDSESSION` -> `EndSessionRequest` -> flush checkpoint, end session. Clean boundary.

### Crash recovery
- Full gap attributed (uncapped), marked `is_recovery=1`
- Formula: `TickCount64 - (lastEntry.startedAtTick + lastEntry.durationMs)`
- Uses last entry's desktop/app values
- Orphaned session gets `ended_at` stamped
- Reports can toggle recovery entries via checkbox

### Report generation
- SQL: `SELECT desktop_name, app_name, SUM(duration_ms) FROM time_entries WHERE started_at BETWEEN @start AND @end GROUP BY desktop_name, app_name ORDER BY desktop_name, total_ms DESC`
- UI groups by desktop, computes subtotals and percentages

---

## 5. User Experience

### First Launch
1. User runs `VirtualDesktopTracker.exe`
2. If elevated -> balloon: "Please run without admin privileges" -> exit
3. Gray tray icon - app in **Stopped** state
4. Balloon: *"VDesk Tracker running. Right-click and Start Tracking to begin."*
5. DB created at `%LOCALAPPDATA%\VirtualDesktopTracker\`
6. Tooltip: *"VDesk Tracker · Stopped"*

### Starting/Stopping Tracking
1. Right-click -> **Start Tracking** -> icon turns green
2. Tooltip: *"VDesk Tracker · Tracking · Work: Chrome (0h 03m)"*
3. Tracking runs silently - no popups, no performance impact
4. Right-click -> **Stop Tracking** -> icon turns gray

### Normal Day While Tracking
- Foreground changes tracked via WinEventHook (no polling, near-zero CPU)
- Tooltip updates on each meaningful switch
- 30s checkpoints written to SQLite
- Sleep, Connected Standby, lock handled gracefully

### Viewing Reports
1. Right-click -> **Show Report...**
2. Modeless window with Today/7d/30d/Custom presets
3. Click **Generate Report** -> grouped grid with percentages
4. "Include recovery entries" checkbox
5. Double-click desktop name -> rename (local label only)
6. Export CSV

### Renaming Desktops
- Tray: **Rename Current Desktop...** -> prompt: "Rename 'Desktop 1' (report label):"
- Report: double-click -> same prompt
- **Does not change Windows Task View name**

### New Desktop Detection
- Win+Ctrl+D -> user switches -> foreground event -> new non-null GUID -> auto-name "Desktop N"
- GUID_NULL windows never create desktop entries

### Sleep/Connected Standby/Lock
- Lid close/screen off/lock -> tracking pauses (flush checkpoint)
- Resume -> tracking resumes seamlessly
- **No phantom time in reports**

### Crash
- On next launch -> recovery balloon with full gap attributed
- Marked `is_recovery=1` - visible in reports, toggleable
- App starts in Stopped state

### OS Shutdown / Exit
- Shutdown: flush + clean session end. No recovery on next boot.
- Exit: flush checkpoint, end session, terminate

---

## 6. Main Window (final)

The app is a normal WinForms window (not a tray app). Closing the window (X) cleanly exits the app.

```
+--------------------------------------------------------+
| VDesk Tracker                              [_][X]     |
+--------------------------------------------------------+
| ● Tracking        (or ● Stopped)                       |
+--------------------------------------------------------+
| Currently active:                                      |
|   Work                                                |
|   chrome.exe                                          |
|   Elapsed: 0h 12m                                      |
+--------------------------------------------------------+
| [Start Tracking / Stop Tracking] [Show Report...]    |
| [Rename Desktop...] [Task Views...]                   |
| [ ] Launch at Startup                                  |
| Closing this window stops tracking and exits the app.  |
+--------------------------------------------------------+
|                                              [Exit]    |
+--------------------------------------------------------+
```

The **Task Views...** button opens the management layer described in [`PLAN-EXTENSION.md`](./PLAN-EXTENSION.md).

---

## 7. Key APIs

| API | Purpose | Source |
|---|---|---|
| `SetWinEventHook` | Foreground detection | user32.dll |
| `UnhookWinEvent` | Cleanup | user32.dll |
| `IVirtualDesktopManager.GetWindowDesktopId` | Desktop GUID | CsWin32 COM |
| `GetWindowThreadProcessId` -> `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` -> `QueryFullProcessImageName` | App path | kernel32.dll |
| `GetAncestor(hwnd, GA_ROOTOWNER)` | Top-level window | user32.dll |
| `GetWindowLongPtr(hwnd, GWL_EXSTYLE)` | Tool-window detection | user32.dll |
| `Environment.TickCount64` | Monotonic elapsed | .NET BCL |
| `PowerModeChanged` | S3 sleep/resume | .NET BCL |
| `RegisterSuspendResumeNotification` | Connected Standby | user32.dll |
| `RegisterPowerSettingNotification` | CS fallback | user32.dll |
| `WTSRegisterSessionNotification` | Lock/unlock | wtsapi32.dll |
| `WM_QUERYENDSESSION` | Shutdown | WinForms WndProc |
| `Microsoft.Data.Sqlite` | SQLite | NuGet |
| `NotifyIcon` | System tray | WinForms |
| `Channel<TrackingEvent>(Bounded(1024, Wait))` | Event marshalling | System.Threading.Channels |
| `Mutex ("Local\VDeskTracker-{user}")` | Single-instance | .NET BCL |
| `ManualResetEventSlim` | Hook unregister sync | .NET BCL |

---

## 8. Implementation Todo

**Phase A — Time tracker (done 2026-06-01):**
1. .NET 9 WinForms project, self-contained single-file publish
2. Manual `[ComImport]` for `IVirtualDesktopManager` (CsWin32 was considered; manual is simpler for this single COM interface)
3. `TrackingEvent` record hierarchy
4. Bounded `Channel<TrackingEvent>(1024, Wait)` + single-DB-writer actor loop with exception handling
5. `SessionManager` with full timer reset on Start, `ActorState` enum, GUID_NULL carry-forward
6. `DataStore` with WAL mode, FK enforcement, 3-rotation backup, `integrity_check`, schema migrations
7. `WinEventHookListener` with coalescing, `ManualResetEventSlim` unhook safety
8. Power handlers: `PowerModeChanged`, `RegisterSuspendResumeNotification`, `RegisterPowerSettingNotification`, `WTSRegisterSessionNotification`, `WM_QUERYENDSESSION`
9. `MainWindow` (replaces `TrayContext`): normal WinForms window with status, start/stop, report, rename, launch-at-startup, exit
10. `ReportWindow` — modeless WinForm, date presets, grouped DataGridView, recovery toggle, export CSV
11. Crash recovery at startup, stamp orphaned session
12. Single-instance `Mutex`
13. Rolling file logger
14. Desktop rename UI (local-only label in `desktop_names`)

**Phase B — Task View management (see `PLAN-EXTENSION.md`):**
15. Add `VirtualDesktop` NuGet package (Grabacr07)
16. Schema migration v1→v2: `task_view_config` table
17. `TaskViewManager` component: enumerate / create / switch / auto-launch
18. `TaskViewWindow` UI + `TaskViewEditDialog`
19. Wire `SessionManager` to skip disabled desktops
20. Wire `Program.cs` to call `RunStartupAutoLaunch` once at start
21. Add "Task Views..." button to `MainWindow`
22. Add per-desktop visibility filter to `ReportWindow`

---

## 9. Acceptance Criteria (UAT)

The user will execute these UAT scripts after build. All must pass.

### UAT-1: First Launch
- Run `VirtualDesktopTracker.exe`
- Main window appears, status "Stopped"
- Log file created at `%LOCALAPPDATA%\VirtualDesktopTracker\logs\`
- File exists: `%LOCALAPPDATA%\VirtualDesktopTracker\VirtualDesktopTracker.db`

### UAT-2: Start/Stop Tracking
- Click "Start Tracking" -> status flips to "Tracking" (green dot)
- "Currently active" shows current desktop / app / elapsed
- Click "Stop Tracking" -> status flips to "Stopped" (gray dot)
- After stop+start, foreground change tracking resumes normally (no stale timer issue)

### UAT-3: Foreground Tracking
- Open 3 different apps sequentially (e.g., Chrome, then Notepad, then Calculator)
- Wait 30s on each
- Open Report window, generate "Today" report
- Verify: each app appears under current desktop with ~30s+ each
- Verify: % column shows reasonable proportions

### UAT-4: New Desktop Detection
- Create a new virtual desktop (Win+Ctrl+D)
- Switch to it, use a different app for 30s
- Switch back to original desktop
- Open Report, generate "Today"
- Verify: both desktops appear with their respective app times

### UAT-5: GUID_NULL Carry-Forward
- Open a UWP app (e.g., Settings, or Microsoft Store)
- Then open a desktop app (e.g., Notepad)
- Open Report
- Verify: UWP app time is attributed to the *previous* desktop, not a fake "Desktop N"

### UAT-6: Sleep/Resume
- Start tracking
- Close laptop lid (sleep) for 30s
- Open laptop
- Verify: tracking resumes, no recovery balloon, no phantom time in report
- Verify: a new session was created (check DB sessions table)

### UAT-7: Lock/Unlock
- Start tracking
- Press Win+L
- Wait 10s
- Unlock
- Verify: tracking paused during lock (no time_entries for that period)
- Verify: report doesn't show "LockApp" as an app

### UAT-8: Crash Recovery
- Start tracking
- Open Task Manager, kill VirtualDesktopTracker.exe
- Wait 30s
- Restart app
- Verify: log shows "Crash recovery: gapMs=..."
- Open Report
- Verify: recovery time is shown in report (with is_recovery=1)
- Verify: uncheck "Include recovery entries" hides it

### UAT-9: Desktop Rename
- Click "Rename Desktop..." -> enter "Work" -> OK
- Verify: report window shows "Work" instead of "Desktop 1"
- Verify: in DB, `desktop_names` table has entry with `name="Work"`

### UAT-10: Shutdown
- Start tracking
- Close the main window (X button)
- Verify: clean shutdown (no recovery on next boot)
- Restart app
- Verify: data is intact, last session has ended_at populated

### UAT-11: Single Instance
- Run `VirtualDesktopTracker.exe`
- Run it again (double-click the EXE)
- Verify: only one main window, second process exits
- Verify: no DB lock errors in logs

### UAT-12: Report CSV Export
- Start tracking, use 2 apps for 1 minute each
- Open Report, generate, click "Export CSV"
- Save to a file
- Open the CSV in Notepad
- Verify: comma-separated rows with desktop, app, time, %

### UAT-13: Lightweight
- Start tracking
- Open Task Manager
- Verify: VirtualDesktopTracker.exe uses <50 MB RAM and <1% CPU at idle
- Verify: no significant disk activity (other than 30s checkpoint writes)

### UAT-14: Elevated
- Right-click VirtualDesktopTracker.exe -> "Run as administrator"
- Verify: log shows a warning about running elevated
- Verify: app continues to run normally (no exit)
- *(Original spec said exit; relaxed to warn-only after first UAT runs.)*

### UAT-15–UAT-18: Task View Management
See [`PLAN-EXTENSION.md`](./PLAN-EXTENSION.md) §7.

---

## 10. Build & Run

```bash
cd C:\Users\ThanosMilios\projects\vdesk-tracker
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
# Executable: C:\Users\ThanosMilios\projects\vdesk-tracker\publish\VirtualDesktopTracker.exe
```

Then run the executable and execute UAT scripts 1-14.
