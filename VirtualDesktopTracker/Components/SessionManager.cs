using System;
using VirtualDesktopTracker.Events;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public enum ActorState
{
    Active,
    Suspended
}

public sealed class SessionManager
{
    private readonly DataStore _store;
    private readonly Action _onWriteComplete;
    private readonly Func<Guid, string?> _resolveName;
    private readonly Func<Guid, bool> _isDesktopEnabled;

    public bool IsTracking { get; private set; }
    public ActorState State { get; private set; } = ActorState.Active;

    public string? CurrentSessionId { get; private set; }
    public Guid CurrentDesktopId { get; private set; }
    public string CurrentDesktopName { get; private set; } = "";
    public string CurrentAppPath { get; private set; } = "";
    public string CurrentAppName { get; private set; } = "";

    public long IntervalStartedAtTick { get; private set; }
    public DateTimeOffset IntervalStartedAtWall { get; private set; }

    public Guid LastNonNullDesktopId { get; private set; }
    public string LastNonNullDesktopName { get; private set; } = "";

    public LastTimeEntryState LastTimeEntry { get; private set; }

    public SessionManager(
        DataStore store,
        Action onWriteComplete,
        Func<Guid, string?> resolveName,
        Func<Guid, bool> isDesktopEnabled)
    {
        _store = store;
        _onWriteComplete = onWriteComplete;
        _resolveName = resolveName;
        _isDesktopEnabled = isDesktopEnabled;
    }

    public void OnStartTracking()
    {
        if (IsTracking) return;
        ResetAll();
        CurrentSessionId = _store.CreateSession("user_start");
        IsTracking = true;
        _onWriteComplete();
    }

    public void OnStopTracking()
    {
        if (!IsTracking) return;
        FlushIfActive();
        if (CurrentSessionId is not null)
        {
            _store.EndSession(CurrentSessionId);
        }
        IsTracking = false;
        ResetAll();
    }

    public void OnForegroundChanged(Guid desktopId, string desktopName, string appPath, string appName)
    {
        if (!IsTracking) return;
        if (State == ActorState.Suspended) return;

        if (desktopId == Guid.Empty)
        {
            if (LastNonNullDesktopId == Guid.Empty) return;
            desktopId = LastNonNullDesktopId;
            desktopName = LastNonNullDesktopName;
        }
        else
        {
            LastNonNullDesktopId = desktopId;
            LastNonNullDesktopName = string.IsNullOrEmpty(desktopName) ? AutoDesktopName(desktopId) : desktopName;
        }

        if (!_isDesktopEnabled(desktopId))
        {
            return;
        }

        var nowTick = Environment.TickCount64;
        var nowWall = DateTimeOffset.UtcNow;
        var elapsed = nowTick - IntervalStartedAtTick;
        if (elapsed > 0)
        {
            _store.InsertTimeEntry(
                CurrentSessionId!,
                CurrentDesktopId,
                CurrentDesktopName,
                CurrentAppPath,
                CurrentAppName,
                IntervalStartedAtWall,
                elapsed,
                isCheckpoint: false,
                isRecovery: false);
        }

        CurrentDesktopId = desktopId;
        CurrentDesktopName = desktopName;
        CurrentAppPath = appPath;
        CurrentAppName = appName;
        IntervalStartedAtTick = nowTick;
        IntervalStartedAtWall = nowWall;
        LastTimeEntry = new LastTimeEntryState
        {
            DesktopId = desktopId,
            DesktopName = desktopName,
            AppPath = appPath,
            AppName = appName,
            StartedAtTick = IntervalStartedAtTick,
            DurationMs = 0
        };
        _onWriteComplete();
    }

    public void OnCheckpointTimer()
    {
        if (!IsTracking) return;
        if (State == ActorState.Suspended) return;
        if (CurrentDesktopId == Guid.Empty) return;

        var nowTick = Environment.TickCount64;
        var elapsed = nowTick - IntervalStartedAtTick;
        if (elapsed <= 0) return;
        _store.InsertTimeEntry(
            CurrentSessionId!,
            CurrentDesktopId,
            CurrentDesktopName,
            CurrentAppPath,
            CurrentAppName,
            IntervalStartedAtWall,
            elapsed,
            isCheckpoint: true,
            isRecovery: false);
        IntervalStartedAtTick = nowTick;
        IntervalStartedAtWall = DateTimeOffset.UtcNow;
        LastTimeEntry = new LastTimeEntryState
        {
            DesktopId = CurrentDesktopId,
            DesktopName = CurrentDesktopName,
            AppPath = CurrentAppPath,
            AppName = CurrentAppName,
            StartedAtTick = IntervalStartedAtTick,
            DurationMs = 0
        };
        _onWriteComplete();
    }

    public void OnSuspend()
    {
        if (State == ActorState.Suspended) return;
        FlushIfActive();
        State = ActorState.Suspended;
    }

    public void OnSessionLock()
    {
        if (State == ActorState.Suspended) return;
        FlushIfActive();
        State = ActorState.Suspended;
    }

    public void OnResume()
    {
        if (State == ActorState.Active) return;
        State = ActorState.Active;
        IntervalStartedAtTick = Environment.TickCount64;
        IntervalStartedAtWall = DateTimeOffset.UtcNow;
        LastNonNullDesktopId = Guid.Empty;
        LastNonNullDesktopName = "";
    }

    public void OnSessionUnlock()
    {
        OnResume();
    }

    public void OnEndSessionRequest()
    {
        if (!IsTracking) return;
        FlushIfActive();
        if (CurrentSessionId is not null)
        {
            _store.EndSession(CurrentSessionId);
        }
        IsTracking = false;
        ResetAll();
    }

    public void OnRenameDesktop(Guid desktopId, string newName)
    {
        _store.RenameDesktopLabel(desktopId, newName);
        _store.UpdateTaskViewDisplayName(desktopId, newName);
        if (CurrentDesktopId == desktopId)
        {
            CurrentDesktopName = newName;
        }
        _onWriteComplete();
    }

    private void FlushIfActive()
    {
        if (!IsTracking) return;
        if (State == ActorState.Suspended) return;
        if (CurrentDesktopId == Guid.Empty) return;
        var nowTick = Environment.TickCount64;
        var elapsed = nowTick - IntervalStartedAtTick;
        if (elapsed > 0)
        {
            _store.InsertTimeEntry(
                CurrentSessionId!,
                CurrentDesktopId,
                CurrentDesktopName,
                CurrentAppPath,
                CurrentAppName,
                IntervalStartedAtWall,
                elapsed,
                isCheckpoint: true,
                isRecovery: false);
            IntervalStartedAtTick = nowTick;
            IntervalStartedAtWall = DateTimeOffset.UtcNow;
            LastTimeEntry = new LastTimeEntryState
            {
                DesktopId = CurrentDesktopId,
                DesktopName = CurrentDesktopName,
                AppPath = CurrentAppPath,
                AppName = CurrentAppName,
                StartedAtTick = IntervalStartedAtTick,
                DurationMs = 0
            };
        }
    }

    private void ResetAll()
    {
        IntervalStartedAtTick = Environment.TickCount64;
        IntervalStartedAtWall = DateTimeOffset.UtcNow;
        LastNonNullDesktopId = Guid.Empty;
        LastNonNullDesktopName = "";
        CurrentDesktopId = Guid.Empty;
        CurrentDesktopName = "";
        CurrentAppPath = "";
        CurrentAppName = "";
        LastTimeEntry = default;
    }

    private string AutoDesktopName(Guid desktopId)
    {
        var tv = _store.GetTaskViewConfig(desktopId);
        if (tv is not null && !string.IsNullOrWhiteSpace(tv.DisplayName))
        {
            return tv.DisplayName;
        }
        var stored = _resolveName(desktopId);
        if (!string.IsNullOrEmpty(stored)) return stored!;
        var n = _store.NextAutoDesktopNumber();
        var name = $"Desktop {n}";
        _store.UpsertDesktopName(desktopId, name);
        return name;
    }
}

public struct LastTimeEntryState
{
    public Guid DesktopId;
    public string DesktopName;
    public string AppPath;
    public string AppName;
    public long StartedAtTick;
    public long DurationMs;
}
