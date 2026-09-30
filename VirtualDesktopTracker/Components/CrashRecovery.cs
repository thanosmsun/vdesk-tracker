using System;
using Microsoft.Data.Sqlite;
using Serilog;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public sealed class CrashRecovery
{
    // The checkpoint timer flushes every 30s, so at most one checkpoint
    // interval can be unaccounted for when the app exits uncleanly.
    // Anything beyond that is shutdown/sleep/away time and must NOT be
    // credited as tracked activity.
    private static readonly TimeSpan MaxRecoveryCredit = TimeSpan.FromSeconds(35);

    private readonly DataStore _store;

    public CrashRecovery(DataStore store)
    {
        _store = store;
    }

    public CrashRecoveryResult? Run()
    {
        try
        {
            var orphans = FindOrphanedSessions();
            if (orphans.Count == 0) return null;

            long creditedMs = 0;
            _store.Write((c, tx) =>
            {
                foreach (var orphan in orphans)
                {
                    var lastEntry = FindLastTimeEntry(c, orphan.SessionId);
                    var endWall = lastEntry?.EndWall ?? orphan.StartedAt;

                    using (var stamp = c.CreateCommand())
                    {
                        stamp.Transaction = tx;
                        stamp.CommandText = "UPDATE sessions SET ended_at = @end WHERE id = @id;";
                        stamp.Parameters.AddWithValue("@end", endWall.ToString("o"));
                        stamp.Parameters.AddWithValue("@id", orphan.SessionId);
                        stamp.ExecuteNonQuery();
                    }

                    if (lastEntry is null) continue;

                    var gap = DateTimeOffset.UtcNow - endWall;
                    if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
                    var credit = gap > MaxRecoveryCredit ? MaxRecoveryCredit : gap;
                    if (credit <= TimeSpan.Zero) continue;

                    var newSessionId = Guid.NewGuid().ToString("N");
                    using (var ins = c.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = @"
INSERT INTO sessions(id, started_at, ended_at, start_reason, created_app, machine_name)
VALUES(@id, @start, NULL, 'crash_recovery', @app, @machine);";
                        ins.Parameters.AddWithValue("@id", newSessionId);
                        ins.Parameters.AddWithValue("@start", endWall.ToString("o"));
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
                        ins2.Parameters.AddWithValue("@sa", endWall.ToString("o"));
                        ins2.Parameters.AddWithValue("@dur", (long)credit.TotalMilliseconds);
                        ins2.ExecuteNonQuery();
                    }

                    creditedMs += (long)credit.TotalMilliseconds;
                }
            });
            return new CrashRecoveryResult(creditedMs);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Crash recovery failed");
            return null;
        }
    }

    private List<OrphanedSession> FindOrphanedSessions()
    {
        var list = new List<OrphanedSession>();
        _store.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, started_at FROM sessions WHERE ended_at IS NULL ORDER BY started_at ASC;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                list.Add(new OrphanedSession(rdr.GetString(0), DateTimeOffset.Parse(rdr.GetString(1))));
            }
        });
        return list;
    }

    private static LastEntryInfo? FindLastTimeEntry(SqliteConnection c, string sessionId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT desktop_id, desktop_name, app_path, app_name,
       CAST(strftime('%s', started_at) AS INTEGER)*1000 + duration_ms AS end_unix_ms
FROM time_entries
WHERE session_id = @sid
ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return new LastEntryInfo(
            Guid.Parse(rdr.GetString(0)),
            rdr.GetString(1),
            rdr.GetString(2),
            rdr.GetString(3),
            DateTimeOffset.FromUnixTimeMilliseconds(rdr.GetInt64(4)));
    }
}

public sealed record OrphanedSession(string SessionId, DateTimeOffset StartedAt);
public sealed record LastEntryInfo(Guid DesktopId, string DesktopName, string AppPath, string AppName, DateTimeOffset EndWall);
public sealed record CrashRecoveryResult(long GapMs);
