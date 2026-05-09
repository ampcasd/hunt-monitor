using System.IO;
using Microsoft.Data.Sqlite;

namespace TibiaSquare.HuntMonitor.Storage;

public static class DatabaseMigrator
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );
        INSERT INTO schema_version (version) VALUES (0);
        """,

        """
        CREATE TABLE hunt_sessions (
            id TEXT PRIMARY KEY,
            character_name TEXT NOT NULL,
            started_at_utc TEXT NOT NULL,
            ended_at_utc TEXT,
            active_duration_seconds INTEGER NOT NULL DEFAULT 0,
            end_reason TEXT,
            is_synced INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE hunt_snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL REFERENCES hunt_sessions(id) ON DELETE CASCADE,
            timestamp_utc TEXT NOT NULL,
            session_time_seconds INTEGER,
            raw_xp_gain INTEGER,
            xp_gain INTEGER,
            raw_xp_per_hour INTEGER,
            xp_per_hour INTEGER,
            loot INTEGER,
            supplies INTEGER,
            balance INTEGER,
            damage INTEGER,
            damage_per_hour INTEGER,
            healing INTEGER,
            healing_per_hour INTEGER
        );

        CREATE TABLE killed_monsters (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id INTEGER NOT NULL REFERENCES hunt_snapshots(id) ON DELETE CASCADE,
            monster_name TEXT NOT NULL,
            kill_count INTEGER NOT NULL
        );

        CREATE INDEX idx_snapshots_session ON hunt_snapshots(session_id);
        CREATE INDEX idx_monsters_snapshot ON killed_monsters(snapshot_id);
        """,

        """
        CREATE TABLE session_screenshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL REFERENCES hunt_sessions(id) ON DELETE CASCADE,
            session_time_seconds INTEGER NOT NULL,
            raw_xp_per_hour INTEGER,
            file_path TEXT NOT NULL,
            is_uploaded INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX idx_screenshots_session ON session_screenshots(session_id);
        """,

        // Migration 3: baseline columns for cumulative value delta correction
        """
        ALTER TABLE hunt_sessions ADD COLUMN baseline_xp_gain INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_loot INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_supplies INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_balance INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_damage INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_healing INTEGER;
        ALTER TABLE hunt_sessions ADD COLUMN baseline_killed_monsters TEXT;
        """,

        // Migration 4: stamina column on hunt_snapshots
        """
        ALTER TABLE hunt_snapshots ADD COLUMN stamina INTEGER;
        """,
    ];

    public static string GetDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "TibiaSquare");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "hunt_monitor.db");
    }

    public static SqliteConnection CreateAndMigrate(string? dbPath = null)
    {
        dbPath ??= GetDatabasePath();
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragmaCmd.ExecuteNonQuery();

        var currentVersion = GetCurrentVersion(connection);

        for (int i = currentVersion + 1; i < Migrations.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Migrations[i];
            cmd.ExecuteNonQuery();

            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE schema_version SET version = @version";
            updateCmd.Parameters.AddWithValue("@version", i);
            updateCmd.ExecuteNonQuery();

            transaction.Commit();
        }

        return connection;
    }

    private static int GetCurrentVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
        if (cmd.ExecuteScalar() == null)
        {
            // Run migration 0 to create schema_version table
            using var initCmd = connection.CreateCommand();
            initCmd.CommandText = Migrations[0];
            initCmd.ExecuteNonQuery();
            return 0;
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var result = versionCmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : 0;
    }
}
