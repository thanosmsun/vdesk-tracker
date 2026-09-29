using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog;
using VirtualDesktopTracker.Events;

namespace VirtualDesktopTracker.Components;

public sealed class Actor
{
    private readonly Channel<TrackingEvent> _channel;
    private readonly DataStore _store;
    private readonly SessionManager _session;
    private readonly WinEventHookListener _listener;
    private System.Threading.Timer? _checkpointTimer;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _terminalError;

    public bool TerminalError => _terminalError;

    public TooltipSnapshot? SnapshotTooltip()
    {
        if (!_session.IsTracking) return null;
        if (_session.CurrentDesktopId == Guid.Empty) return null;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _session.IntervalStartedAtTick);
        return new TooltipSnapshot(
            string.IsNullOrEmpty(_session.CurrentDesktopName) ? "-" : _session.CurrentDesktopName,
            string.IsNullOrEmpty(_session.CurrentAppName) ? "-" : _session.CurrentAppName,
            FormatElapsed(elapsed));
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        }
        if (ts.TotalMinutes >= 1)
        {
            return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
        }
        return $"{(int)ts.TotalSeconds}s";
    }

    public Actor(
        Channel<TrackingEvent> channel,
        DataStore store,
        SessionManager session,
        WinEventHookListener listener)
    {
        _channel = channel;
        _store = store;
        _session = session;
        _listener = listener;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _checkpointTimer?.Dispose(); } catch { }
        _checkpointTimer = null;
        try { _cts?.Cancel(); } catch { }
        try { _channel.Writer.TryComplete(); } catch { }
        try { _loopTask?.Wait(2000); } catch { }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void StartCheckpointTimer()
    {
        try { _checkpointTimer?.Dispose(); } catch { }
        _checkpointTimer = new System.Threading.Timer(_ =>
        {
            TryPost(new CheckpointTimerEvent());
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void StopCheckpointTimer()
    {
        try { _checkpointTimer?.Dispose(); } catch { }
        _checkpointTimer = null;
    }

    public bool TryPost(TrackingEvent evt) => _channel.Writer.TryWrite(evt);

    public void EnqueueSuspend() => TryPost(new SuspendEvent(DateTimeOffset.UtcNow));
    public void EnqueueResume() => TryPost(new ResumeEvent(DateTimeOffset.UtcNow));
    public void EnqueueSessionLock() => TryPost(new SessionLockEvent());
    public void EnqueueSessionUnlock() => TryPost(new SessionUnlockEvent(DateTimeOffset.UtcNow));
    public void EnqueueEndSessionRequest() => TryPost(new EndSessionRequestEvent(DateTimeOffset.UtcNow));

    private void MarkWrite()
    {
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var evt))
                {
                    if (ct.IsCancellationRequested) return;
                    ProcessEvent(evt);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Actor loop crashed");
            _terminalError = true;
        }
    }

    private void ProcessEvent(TrackingEvent evt)
    {
        try
        {
            if (_session.State == ActorState.Suspended
                && evt is not ResumeEvent
                && evt is not SuspendEvent
                && evt is not SessionLockEvent
                && evt is not SessionUnlockEvent
                && evt is not StopTrackingEvent
                && evt is not EndSessionRequestEvent
                && evt is not PingEvent)
            {
                return;
            }

            switch (evt)
            {
                case StartTrackingEvent:
                    _session.OnStartTracking();
                    _listener.ResetCoalescing();
                    StartCheckpointTimer();
                    break;
                case StopTrackingEvent:
                    StopCheckpointTimer();
                    _session.OnStopTracking();
                    break;
                case ForegroundChangedEvent fc:
                    _session.OnForegroundChanged(fc.DesktopId, fc.DesktopName, fc.AppPath, fc.AppName);
                    break;
                case CheckpointTimerEvent:
                    _session.OnCheckpointTimer();
                    break;
                case SuspendEvent:
                    _session.OnSuspend();
                    _listener.ResetCoalescing();
                    break;
                case ResumeEvent:
                    _session.OnResume();
                    _listener.ResetCoalescing();
                    break;
                case SessionLockEvent:
                    _session.OnSessionLock();
                    _listener.ResetCoalescing();
                    break;
                case SessionUnlockEvent:
                    _session.OnSessionUnlock();
                    _listener.ResetCoalescing();
                    break;
                case EndSessionRequestEvent:
                    _session.OnEndSessionRequest();
                    break;
                case ResetCoalescingEvent:
                    _listener.ResetCoalescing();
                    break;
                case RenameDesktopEvent rd:
                    _session.OnRenameDesktop(rd.DesktopId, rd.Name);
                    break;
                case PingEvent:
                    break;
            }
            MarkWrite();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException sqlex) when (IsFullDisk(sqlex))
        {
            Log.Error(sqlex, "SQLite full disk, stopping tracking");
            _session.OnStopTracking();
            _terminalError = true;
        }
        catch (SqliteException sqlex)
        {
            Log.Error(sqlex, "SQLite error in actor");
            _terminalError = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Actor event error");
        }
    }

    private static bool IsFullDisk(SqliteException ex)
    {
        return ex.SqliteErrorCode == 13 || (ex.Message?.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}

public sealed record TooltipSnapshot(string DesktopName, string AppName, string Elapsed);
