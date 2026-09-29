using System;

namespace VirtualDesktopTracker.Events;

public abstract record TrackingEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ForegroundChangedEvent(
    Guid DesktopId,
    string DesktopName,
    string AppPath,
    string AppName) : TrackingEvent;

public sealed record CheckpointTimerEvent() : TrackingEvent;

public sealed record SuspendEvent(DateTimeOffset At) : TrackingEvent;

public sealed record ResumeEvent(DateTimeOffset At) : TrackingEvent;

public sealed record SessionLockEvent() : TrackingEvent;

public sealed record SessionUnlockEvent(DateTimeOffset At) : TrackingEvent;

public sealed record EndSessionRequestEvent(DateTimeOffset At) : TrackingEvent;

public sealed record StartTrackingEvent() : TrackingEvent;

public sealed record StopTrackingEvent() : TrackingEvent;

public sealed record ResetCoalescingEvent() : TrackingEvent;

public sealed record RenameDesktopEvent(Guid DesktopId, string Name) : TrackingEvent;

public sealed record PingEvent() : TrackingEvent;
