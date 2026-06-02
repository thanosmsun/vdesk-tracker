using System;
using Microsoft.Data.Sqlite;
using Serilog;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public sealed class CrashRecovery
{
    private readonly DataStore _store;

    public CrashRecovery(DataStore store)
    {
        _store = store;
    }

    public CrashRecoveryResult? Run()
    {
        try
        {
            var orphan = FindOrphanedSession();
            if (orphan is null) return null;
            var lastEntry = FindLastTimeEntry(orphan.SessionId);
            _store.Write((c, tx) =>
            {
                if (lastEntry is not null)
                {
                    var nowTick = Environment.TickCount64;
                    var gap = nowTick - (lastEntry.StartedAtTick + lastEntry.DurationMs);
                    if (gap < 0) gap = 0;

                    if (orphan.SessionId is not null)
                    {
                        using (var stamp = c.CreateCommand())
                        {
                            stamp.Transaction = tx;
                            stamp.CommandText = "UPDATE sessions SET ended_at = @now WHERE id = @id;";
                            stamp.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                            stamp.Parameters.AddWithValue("@id", orphan.SessionId);
                            stamp.ExecuteNonQuery();
                        }
                    }

                    var newSessionId = Guid.NewGuid().ToString("N");
                    using (var ins = c.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = @"
INSERT INTO sessions(id, started_at, ended_at, start_reason, created_app, machine_name)
VALUES(@id, @now, NULL, 'crash_recovery', @app, @machine);";
                        ins.Parameters.AddWithValue("@id", newSessionId);
                        ins.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                        ins.Parameters.AddWithValue("@app", "VirtualDesktopTracker");
                        ins.Parameters.AddWithValue("@machine", Environment.MachineName);
                        ins.ExecuteNonQuery();
                    }

                    using (var ins2 = c.CreateCommand())
                    {
                        ins2.Transaction = tx;
                        ins2.CommandText = @"
INSERT INTO time_entries(session_id, desktop_id, desktop_name, app_path, app_name, started_at, duration_ms, is_checkpoint, is_recovery)
VALUES(@sid, @did, @dn, @ap, @an, @sa, @dur, 0, 1);";
                        ins2.Parameters.AddWithValue("@sid", newSessionId);
                        ins2.Parameters.AddWithValue("@did", lastEntry.DesktopId.ToString());
                        ins2.Parameters.AddWithValue("@dn", lastEntry.DesktopName);
                        ins2.Parameters.AddWithValue("@ap", lastEntry.AppPath);
                        ins2.Parameters.AddWithValue("@an", lastEntry.AppName);
                        ins2.Parameters.AddWithValue("@sa", DateTimeOffset.UtcNow.ToString("o"));
                        ins2.Parameters.AddWithValue("@dur", gap);
                        ins2.ExecuteNonQuery();
                    }
                }
                else
                {
                    if (orphan.SessionId is not null)
                    {
                        using var stamp = c.CreateCommand();
                        stamp.Transaction = tx;
                        stamp.CommandText = "UPDATE sessions SET ended_at = @now WHERE id = @id;";
                        stamp.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                        stamp.Parameters.AddWithValue("@id", orphan.SessionId);
                        stamp.ExecuteNonQuery();
                    }
                }
            });
            return lastEntry is null ? new CrashRecoveryResult(0) : new CrashRecoveryResult(Environment.TickCount64 - (lastEntry.StartedAtTick + lastEntry.DurationMs));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Crash recovery failed");
            return null;
        }
    }

    private OrphanedSession? FindOrphanedSession()
    {
        string? id = null;
        var startedAt = DateTimeOffset.MinValue;
        _store.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, started_at FROM sessions WHERE ended_at IS NULL ORDER BY started_at DESC LIMIT 1;";
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                id = rdr.GetString(0);
                startedAt = DateTimeOffset.Parse(rdr.GetString(1));
            }
        });
        return id is null ? null : new OrphanedSession(id, startedAt);
    }

    private LastEntryInfo? FindLastTimeEntry(string? sessionId)
    {
        if (sessionId is null) return null;
        LastEntryInfo? result = null;
        _store.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
SELECT desktop_id, desktop_name, app_path, app_name,
       CAST(strftime('%s', started_at) AS INTEGER)*1000 AS started_unix_ms,
       duration_ms
FROM time_entries
WHERE session_id = @sid
ORDER BY id DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                var startedMs = rdr.GetInt64(4);
                var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(startedMs);
                var startedTick = DateTimeToTickApprox(startedAt);
                result = new LastEntryInfo(
                    Guid.Parse(rdr.GetString(0)),
                    rdr.GetString(1),
                    rdr.GetString(2),
                    rdr.GetString(3),
                    startedTick,
                    rdr.GetInt64(5));
            }
        });
        return result;
    }

    private static long DateTimeToTickApprox(DateTimeOffset wall)
    {
        var nowWall = DateTimeOffset.UtcNow;
        var nowTick = Environment.TickCount64;
        var diff = nowWall - wall;
        return nowTick - (long)diff.TotalMilliseconds;
    }
}

public sealed record OrphanedSession(string SessionId, DateTimeOffset StartedAt);
public sealed record LastEntryInfo(Guid DesktopId, string DesktopName, string AppPath, string AppName, long StartedAtTick, long DurationMs);
public sealed record CrashRecoveryResult(long GapMs);
