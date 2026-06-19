using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Serilog;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public sealed class DataStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private SqliteConnection? _writer;
    private readonly object _writerLock = new();
    private bool _disposed;

    public string DatabasePath => _dbPath;

    public DataStore(string dbPath)
    {
        _dbPath = dbPath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ConnectionString;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        RotateBackups();

        OpenWriter();
        EnsureSchema();

        if (!IntegrityCheckOk())
        {
            Log.Warning("DB integrity check failed, attempting to restore from backup");
            CloseWriter();
            if (RestoreLatestBackup())
            {
                OpenWriter();
                EnsureSchema();
            }
            else
            {
                Log.Warning("No valid backup found, reopening existing DB");
                OpenWriter();
            }
        }
    }

    private void OpenWriter()
    {
        lock (_writerLock)
        {
            _writer?.Dispose();
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
                pragma.ExecuteNonQuery();
            }
            _writer = conn;
        }
    }

    private void CloseWriter()
    {
        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public SqliteConnection GetReader()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DataStore));
        lock (_writerLock)
        {
            if (_writer is null) throw new InvalidOperationException("Writer not open");
            using var tx = _writer.BeginTransaction();
            work(_writer, tx);
            tx.Commit();
        }
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DataStore));
        lock (_writerLock)
        {
            if (_writer is null) throw new InvalidOperationException("Writer not open");
            using var tx = _writer.BeginTransaction();
            var result = work(_writer, tx);
            tx.Commit();
            return result;
        }
    }

    private void EnsureSchema()
    {
        if (_writer is null) throw new InvalidOperationException("Writer not open");
        int version;
        using (var cmd = _writer.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            var raw = cmd.ExecuteScalar();
            version = raw is long l ? (int)l : (raw is int i ? i : 0);
        }

        if (version < 1)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = SchemaV1;
            cmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = SchemaV2;
            cmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            using (var cmd = _writer.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version = 2;";
                cmd.ExecuteNonQuery();
            }
        }

        if (version < 3)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = @"
UPDATE time_entries
SET desktop_name = COALESCE(
    (SELECT tvc.display_name FROM task_view_config tvc WHERE tvc.desktop_id = time_entries.desktop_id),
    (SELECT dn.name FROM desktop_names dn WHERE dn.desktop_id = time_entries.desktop_id),
    'Unknown'
)
WHERE desktop_name IS NULL OR desktop_name = '';";
            cmd.ExecuteNonQuery();
        }

        if (version < 3)
        {
            using (var cmd = _writer.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version = 3;";
                cmd.ExecuteNonQuery();
            }
        }

        UpsertDefaultSettings();
    }

    private const string SchemaV1 = @"
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS sessions (
    id            TEXT PRIMARY KEY,
    started_at    TEXT NOT NULL,
    ended_at      TEXT,
    start_reason  TEXT NOT NULL DEFAULT 'startup' CHECK(start_reason IN (
                      'startup','user_start','resume','crash_recovery')),
    created_app   TEXT NOT NULL,
    machine_name  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS time_entries (
    id            INTEGER PRIMARY KEY,
    session_id    TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    desktop_id    TEXT NOT NULL,
    desktop_name  TEXT NOT NULL,
    app_path      TEXT NOT NULL DEFAULT '',
    app_name      TEXT NOT NULL,
    started_at    TEXT NOT NULL,
    duration_ms   INTEGER NOT NULL CHECK(duration_ms >= 0),
    is_checkpoint INTEGER NOT NULL DEFAULT 0 CHECK(is_checkpoint IN (0,1)),
    is_recovery   INTEGER NOT NULL DEFAULT 0 CHECK(is_recovery IN (0,1))
);
CREATE INDEX IF NOT EXISTS idx_te_started ON time_entries(started_at);
CREATE INDEX IF NOT EXISTS idx_te_session ON time_entries(session_id);

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
";

    private const string SchemaV2 = @"
CREATE TABLE IF NOT EXISTS task_view_config (
    desktop_id        TEXT PRIMARY KEY,
    display_name      TEXT NOT NULL,
    is_enabled        INTEGER NOT NULL DEFAULT 1 CHECK(is_enabled IN (0,1)),
    auto_launch       TEXT NOT NULL DEFAULT '[]',
    created_at        TEXT NOT NULL,
    updated_at        TEXT NOT NULL
);
";

    private void UpsertDefaultSettings()
    {
        if (_writer is null) return;
        using var tx = _writer.BeginTransaction();
        using (var cmd = _writer.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO meta(key, value) VALUES('schema_built_at', @t)
ON CONFLICT(key) DO NOTHING;";
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private bool IntegrityCheckOk()
    {
        if (_writer is null) return false;
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar() as string;
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    private void RotateBackups()
    {
        try
        {
            if (File.Exists(AppPaths.BackupPath(3)))
            {
                File.Delete(AppPaths.BackupPath(3));
            }
            if (File.Exists(AppPaths.BackupPath(2)))
            {
                File.Move(AppPaths.BackupPath(2), AppPaths.BackupPath(3));
            }
            if (File.Exists(AppPaths.BackupPath(1)))
            {
                File.Move(AppPaths.BackupPath(1), AppPaths.BackupPath(2));
            }
            if (File.Exists(_dbPath))
            {
                File.Copy(_dbPath, AppPaths.BackupPath(1), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Backup rotation failed");
        }
    }

    private bool RestoreLatestBackup()
    {
        for (int i = 1; i <= 3; i++)
        {
            var p = AppPaths.BackupPath(i);
            if (!File.Exists(p)) continue;
            if (!IsBackupFileOk(p))
            {
                Log.Warning("Backup slot {Slot} failed integrity check, skipping", i);
                continue;
            }
            try
            {
                var walPath = _dbPath + "-wal";
                var shmPath = _dbPath + "-shm";
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
                File.Copy(p, _dbPath, overwrite: true);
                Log.Information("Restored DB from backup slot {Slot}", i);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to restore from backup slot {Slot}", i);
            }
        }
        return false;
    }

    private static bool IsBackupFileOk(string path)
    {
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            };
            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string CreateSession(string startReason)
    {
        var id = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow.ToString("o");
        var machine = Environment.MachineName;
        var appName = "VirtualDesktopTracker";
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO sessions(id, started_at, ended_at, start_reason, created_app, machine_name)
VALUES(@id, @started, NULL, @reason, @app, @machine);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@started", startedAt);
            cmd.Parameters.AddWithValue("@reason", startReason);
            cmd.Parameters.AddWithValue("@app", appName);
            cmd.Parameters.AddWithValue("@machine", machine);
            cmd.ExecuteNonQuery();
        });
        return id;
    }

    public void EndSession(string sessionId)
    {
        var endedAt = DateTimeOffset.UtcNow.ToString("o");
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE sessions SET ended_at = @ended WHERE id = @id AND ended_at IS NULL;";
            cmd.Parameters.AddWithValue("@ended", endedAt);
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();
        });
    }

    public void InsertTimeEntry(
        string sessionId,
        Guid desktopId,
        string desktopName,
        string appPath,
        string appName,
        DateTimeOffset startedAt,
        long durationMs,
        bool isCheckpoint,
        bool isRecovery)
    {
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO time_entries(session_id, desktop_id, desktop_name, app_path, app_name, started_at, duration_ms, is_checkpoint, is_recovery)
VALUES(@sid, @did, @dn, @ap, @an, @sa, @dur, @cp, @rec);";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@did", desktopId.ToString());
            cmd.Parameters.AddWithValue("@dn", desktopName);
            cmd.Parameters.AddWithValue("@ap", appPath ?? "");
            cmd.Parameters.AddWithValue("@an", appName);
            cmd.Parameters.AddWithValue("@sa", startedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@dur", durationMs);
            cmd.Parameters.AddWithValue("@cp", isCheckpoint ? 1 : 0);
            cmd.Parameters.AddWithValue("@rec", isRecovery ? 1 : 0);
            cmd.ExecuteNonQuery();
        });
    }

    public void UpsertDesktopName(Guid desktopId, string name)
    {
        if (desktopId == Guid.Empty) return;
        var now = DateTimeOffset.UtcNow.ToString("o");
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO desktop_names(desktop_id, name, created_at, updated_at)
VALUES(@id, @name, @now, @now)
ON CONFLICT(desktop_id) DO UPDATE SET name=@name, updated_at=@now;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        });
    }

    public string? GetDesktopName(Guid desktopId)
    {
        if (_disposed) return null;
        string? value = null;
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name FROM desktop_names WHERE desktop_id=@id;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            var v = cmd.ExecuteScalar();
            if (v is string s) value = s;
        });
        return value;
    }

    public int CountDesktopNames()
    {
        if (_disposed) return 0;
        int n = 0;
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM desktop_names;";
            var v = cmd.ExecuteScalar();
            if (v is long l) n = (int)l;
        });
        return n;
    }

    public int NextAutoDesktopNumber()
    {
        if (_disposed) return 1;
        int n = 0;
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(CAST(SUBSTR(name, 9) AS INTEGER)), 0) FROM desktop_names WHERE name LIKE 'Desktop %' AND SUBSTR(name, 9) GLOB '[0-9]*';";
            var v = cmd.ExecuteScalar();
            if (v is long l) n = (int)l;
        });
        return n + 1;
    }

    public void Read(Action<SqliteConnection> work)
    {
        if (_disposed) return;
        using var conn = GetReader();
        work(conn);
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        if (_disposed) return default!;
        using var conn = GetReader();
        return work(conn);
    }

    public bool UpdateTaskViewDisplayName(Guid desktopId, string newName)
    {
        if (desktopId == Guid.Empty) return false;
        var now = DateTimeOffset.UtcNow.ToString("o");
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE task_view_config SET display_name=@name, updated_at=@now WHERE desktop_id=@id
  AND display_name != @name;";
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.ExecuteNonQuery();
        });
        return true;
    }

    public bool RenameDesktopLabel(Guid desktopId, string newName)
    {
        if (desktopId == Guid.Empty) return false;
        var now = DateTimeOffset.UtcNow.ToString("o");
        bool updated = false;
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE desktop_names SET name=@name, updated_at=@now WHERE desktop_id=@id;
SELECT changes();";
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            var changes = cmd.ExecuteScalar();
            updated = changes is long l && l > 0;
        });
        if (updated) return true;
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO desktop_names(desktop_id, name, created_at, updated_at)
VALUES(@id, @name, @now, @now);";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        });
        return true;
    }

    public List<ReportRow> QueryReport(DateTimeOffset startInclusive, DateTimeOffset endExclusive, bool includeRecovery)
    {
        var raw = new List<(DateTime LocalDate, string TaskViewName, string AppName, long TotalMs)>();
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
SELECT te.started_at, COALESCE(tvc.display_name, te.desktop_name) AS task_view_name, te.app_name, te.duration_ms
FROM time_entries te
LEFT JOIN task_view_config tvc ON tvc.desktop_id = te.desktop_id
WHERE te.started_at >= @start AND te.started_at < @end
  AND (@incl = 1 OR te.is_recovery = 0)
ORDER BY te.started_at;";
            cmd.Parameters.AddWithValue("@start", startInclusive.ToString("o"));
            cmd.Parameters.AddWithValue("@end", endExclusive.ToString("o"));
            cmd.Parameters.AddWithValue("@incl", includeRecovery ? 1 : 0);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var startedStr = rdr.GetString(0);
                var startedDto = DateTimeOffset.Parse(startedStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                raw.Add((startedDto.ToLocalTime().Date, rdr.GetString(1), rdr.GetString(2), rdr.GetInt64(3)));
            }
        });
        return raw
            .GroupBy(r => (r.LocalDate, r.TaskViewName, r.AppName))
            .Select(g => new ReportRow
            {
                Date = g.Key.LocalDate,
                TaskViewName = g.Key.TaskViewName,
                AppName = g.Key.AppName,
                TotalMs = g.Sum(x => x.TotalMs)
            })
            .OrderBy(r => r.Date)
            .ThenBy(r => r.TaskViewName)
            .ThenByDescending(r => r.TotalMs)
            .ToList();
    }

    public List<DesktopEntry> ListDesktops()
    {
        var list = new List<DesktopEntry>();
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT desktop_id, name FROM desktop_names ORDER BY name;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                list.Add(new DesktopEntry
                {
                    DesktopId = Guid.Parse(rdr.GetString(0)),
                    Name = rdr.GetString(1)
                });
            }
        });
        return list;
    }

    public List<TaskViewConfigRow> GetAllTaskViewConfigs()
    {
        var list = new List<TaskViewConfigRow>();
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT desktop_id, display_name, is_enabled, auto_launch FROM task_view_config ORDER BY display_name;";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                list.Add(new TaskViewConfigRow
                {
                    DesktopId = Guid.Parse(rdr.GetString(0)),
                    DisplayName = rdr.GetString(1),
                    IsEnabled = rdr.GetInt32(2) != 0,
                    AutoLaunch = rdr.GetString(3)
                });
            }
        });
        return list;
    }

    public TaskViewConfigRow? GetTaskViewConfig(Guid desktopId)
    {
        if (_disposed) return null;
        TaskViewConfigRow? row = null;
        Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT desktop_id, display_name, is_enabled, auto_launch FROM task_view_config WHERE desktop_id=@id;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                row = new TaskViewConfigRow
                {
                    DesktopId = Guid.Parse(rdr.GetString(0)),
                    DisplayName = rdr.GetString(1),
                    IsEnabled = rdr.GetInt32(2) != 0,
                    AutoLaunch = rdr.GetString(3)
                };
            }
        });
        return row;
    }

    public void EnsureTaskViewConfigRow(Guid desktopId, string displayName, bool isEnabled = true, string autoLaunch = "[]")
    {
        if (desktopId == Guid.Empty) return;
        var now = DateTimeOffset.UtcNow.ToString("o");
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO task_view_config(desktop_id, display_name, is_enabled, auto_launch, created_at, updated_at)
VALUES(@id, @name, @en, @al, @now, @now)
ON CONFLICT(desktop_id) DO NOTHING;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.Parameters.AddWithValue("@name", displayName);
            cmd.Parameters.AddWithValue("@en", isEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@al", autoLaunch);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        });
    }

    public void UpsertTaskViewConfig(Guid desktopId, string displayName, bool isEnabled, string autoLaunch)
    {
        if (desktopId == Guid.Empty) return;
        var now = DateTimeOffset.UtcNow.ToString("o");
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO task_view_config(desktop_id, display_name, is_enabled, auto_launch, created_at, updated_at)
VALUES(@id, @name, @en, @al, @now, @now)
ON CONFLICT(desktop_id) DO UPDATE SET
    display_name=@name, is_enabled=@en, auto_launch=@al, updated_at=@now;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.Parameters.AddWithValue("@name", displayName);
            cmd.Parameters.AddWithValue("@en", isEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@al", autoLaunch);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        });
    }

    public void DeleteTaskViewConfig(Guid desktopId)
    {
        if (desktopId == Guid.Empty) return;
        Write((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM task_view_config WHERE desktop_id=@id;";
            cmd.Parameters.AddWithValue("@id", desktopId.ToString());
            cmd.ExecuteNonQuery();
        });
    }

    public bool IsDesktopEnabled(Guid desktopId)
    {
        if (desktopId == Guid.Empty) return true;
        var row = GetTaskViewConfig(desktopId);
        return row is null || row.IsEnabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_writerLock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}

public sealed class ReportRow
{
    public DateTime Date { get; set; }
    public string TaskViewName { get; set; } = "";
    public string AppName { get; set; } = "";
    public long TotalMs { get; set; }
}

public sealed class DesktopEntry
{
    public Guid DesktopId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class TaskViewConfigRow
{
    public Guid DesktopId { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string AutoLaunch { get; set; } = "[]";
}
