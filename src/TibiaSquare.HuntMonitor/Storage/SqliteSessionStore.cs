using System.Text.Json;
using Microsoft.Data.Sqlite;
using TibiaSquare.HuntMonitor.Models;

namespace TibiaSquare.HuntMonitor.Storage;

public record ScreenshotRecord(long Id, int SessionTimeSeconds, long? RawXpPerHour, string FilePath, bool IsUploaded);

public sealed class SqliteSessionStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteSessionStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public void CreateSession(HuntSession session)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hunt_sessions (id, character_name, started_at_utc, active_duration_seconds, is_synced,
                baseline_xp_gain, baseline_loot, baseline_supplies, baseline_balance,
                baseline_damage, baseline_healing, baseline_killed_monsters)
            VALUES (@id, @name, @started, @duration, 0,
                @baselineXpGain, @baselineLoot, @baselineSupplies, @baselineBalance,
                @baselineDamage, @baselineHealing, @baselineMonsters)
            """;
        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@name", session.CharacterName);
        cmd.Parameters.AddWithValue("@started", session.StartedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@duration", session.ActiveDurationSeconds);
        AddBaselineParameters(cmd, session.Baseline);
        cmd.ExecuteNonQuery();
    }

    public void EndSession(string sessionId, string endReason, int activeDurationSeconds,
        SessionBaseline? baseline = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE hunt_sessions
            SET ended_at_utc = @ended, end_reason = @reason, active_duration_seconds = @duration,
                baseline_xp_gain = @baselineXpGain, baseline_loot = @baselineLoot,
                baseline_supplies = @baselineSupplies, baseline_balance = @baselineBalance,
                baseline_damage = @baselineDamage, baseline_healing = @baselineHealing,
                baseline_killed_monsters = @baselineMonsters,
                updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@ended", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@reason", endReason);
        cmd.Parameters.AddWithValue("@duration", activeDurationSeconds);
        AddBaselineParameters(cmd, baseline);
        cmd.ExecuteNonQuery();
    }

    public long InsertSnapshot(string sessionId, HuntSnapshot snapshot, DateTime sessionStartUtc)
    {
        var now = DateTime.UtcNow;
        var elapsedSeconds = (int)(now - sessionStartUtc).TotalSeconds;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hunt_snapshots
                (session_id, timestamp_utc, session_time_seconds,
                 raw_xp_gain, xp_gain, raw_xp_per_hour, xp_per_hour,
                 loot, supplies, balance,
                 damage, damage_per_hour, healing, healing_per_hour,
                 stamina)
            VALUES
                (@sessionId, @timestamp, @sessionTime,
                 @rawXpGain, @xpGain, @rawXpPerHour, @xpPerHour,
                 @loot, @supplies, @balance,
                 @damage, @damagePerHour, @healing, @healingPerHour,
                 @stamina);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@timestamp", now.ToString("o"));
        cmd.Parameters.AddWithValue("@sessionTime", elapsedSeconds);
        cmd.Parameters.AddWithValue("@rawXpGain", (object?)snapshot.RawXpGain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@xpGain", (object?)snapshot.XpGain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rawXpPerHour", (object?)snapshot.RawXpPerHour ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@xpPerHour", (object?)snapshot.XpPerHour ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@loot", (object?)snapshot.Loot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@supplies", (object?)snapshot.Supplies ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@balance", (object?)snapshot.Balance ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@damage", (object?)snapshot.Damage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@damagePerHour", (object?)snapshot.DamagePerHour ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@healing", (object?)snapshot.Healing ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@healingPerHour", (object?)snapshot.HealingPerHour ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stamina", (object?)snapshot.Stamina ?? DBNull.Value);

        var snapshotId = (long)cmd.ExecuteScalar()!;

        InsertMonsters(snapshotId, snapshot.KilledMonsters);

        return snapshotId;
    }

    private void InsertMonsters(long snapshotId, IReadOnlyList<KilledMonster> monsters)
    {
        if (monsters.Count == 0)
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO killed_monsters (snapshot_id, monster_name, kill_count)
            VALUES (@snapshotId, @name, @count)
            """;

        var snapshotParam = cmd.Parameters.Add("@snapshotId", SqliteType.Integer);
        var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
        var countParam = cmd.Parameters.Add("@count", SqliteType.Integer);

        using var transaction = _connection.BeginTransaction();
        cmd.Transaction = transaction;

        foreach (var monster in monsters)
        {
            snapshotParam.Value = snapshotId;
            nameParam.Value = monster.Name;
            countParam.Value = monster.Count;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<HuntSession> GetUnfinishedSessions()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, character_name, started_at_utc, active_duration_seconds,
                   baseline_xp_gain, baseline_loot, baseline_supplies, baseline_balance,
                   baseline_damage, baseline_healing, baseline_killed_monsters
            FROM hunt_sessions
            WHERE ended_at_utc IS NULL
            """;

        var sessions = new List<HuntSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new HuntSession
            {
                Id = reader.GetString(0),
                CharacterName = reader.GetString(1),
                StartedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                ActiveDurationSeconds = reader.GetInt32(3),
                Baseline = ReadBaseline(reader, 4),
            });
        }

        return sessions;
    }

    public DateTime? GetLastSnapshotTimestamp(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp_utc
            FROM hunt_snapshots
            WHERE session_id = @sessionId
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var result = cmd.ExecuteScalar();
        if (result is string timestamp)
            return DateTime.Parse(timestamp).ToUniversalTime();
        return null;
    }

    public HuntSnapshot? GetLastSnapshot(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_time_seconds, raw_xp_gain, xp_gain, raw_xp_per_hour, xp_per_hour,
                   loot, supplies, balance, damage, damage_per_hour, healing, healing_per_hour,
                   stamina
            FROM hunt_snapshots
            WHERE session_id = @sessionId
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var snapshotId = reader.GetInt64(0);
        var monsters = GetMonsters(snapshotId);

        return new HuntSnapshot
        {
            SessionTime = TimeSpan.FromSeconds(reader.IsDBNull(1) ? 0 : reader.GetInt32(1)),
            RawXpGain = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            XpGain = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            RawXpPerHour = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            XpPerHour = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            Loot = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Supplies = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            Balance = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            Damage = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            DamagePerHour = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            Healing = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            HealingPerHour = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            Stamina = reader.IsDBNull(13) ? null : reader.GetInt32(13),
            KilledMonsters = monsters,
        };
    }

    private List<KilledMonster> GetMonsters(long snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT monster_name, kill_count
            FROM killed_monsters
            WHERE snapshot_id = @snapshotId
            """;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);

        var monsters = new List<KilledMonster>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            monsters.Add(new KilledMonster(reader.GetString(0), reader.GetInt32(1)));
        }

        return monsters;
    }

    public List<HuntSession> GetUnsyncedCompletedSessions()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, character_name, started_at_utc, ended_at_utc,
                   active_duration_seconds, end_reason, is_synced,
                   baseline_xp_gain, baseline_loot, baseline_supplies, baseline_balance,
                   baseline_damage, baseline_healing, baseline_killed_monsters
            FROM hunt_sessions
            WHERE ended_at_utc IS NOT NULL AND is_synced = 0
            """;

        var sessions = new List<HuntSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new HuntSession
            {
                Id = reader.GetString(0),
                CharacterName = reader.GetString(1),
                StartedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                EndedAtUtc = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                ActiveDurationSeconds = reader.GetInt32(4),
                EndReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsSynced = reader.GetInt32(6) == 1,
                Baseline = ReadBaseline(reader, 7),
            });
        }

        return sessions;
    }

    public List<HuntSession> GetCompletedSessionsForCharacter(string characterName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, character_name, started_at_utc, ended_at_utc,
                   active_duration_seconds, end_reason, is_synced,
                   baseline_xp_gain, baseline_loot, baseline_supplies, baseline_balance,
                   baseline_damage, baseline_healing, baseline_killed_monsters
            FROM hunt_sessions
            WHERE character_name = @name AND ended_at_utc IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@name", characterName);

        var sessions = new List<HuntSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new HuntSession
            {
                Id = reader.GetString(0),
                CharacterName = reader.GetString(1),
                StartedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                EndedAtUtc = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                ActiveDurationSeconds = reader.GetInt32(4),
                EndReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsSynced = reader.GetInt32(6) == 1,
                Baseline = ReadBaseline(reader, 7),
            });
        }

        return sessions;
    }

    public List<StoredSnapshot> GetAllSnapshots(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_time_seconds, raw_xp_gain, xp_gain, raw_xp_per_hour, xp_per_hour,
                   loot, supplies, balance, damage, damage_per_hour, healing, healing_per_hour,
                   stamina
            FROM hunt_snapshots
            WHERE session_id = @sessionId
            ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var snapshots = new List<StoredSnapshot>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var snapshotId = reader.GetInt64(0);
            var monsters = GetMonsters(snapshotId);

            snapshots.Add(new StoredSnapshot
            {
                Id = snapshotId,
                SessionId = sessionId,
                SessionTimeSeconds = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                RawXpGain = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                XpGain = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RawXpPerHour = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                XpPerHour = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Loot = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Supplies = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Balance = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                Damage = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                DamagePerHour = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Healing = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                HealingPerHour = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                Stamina = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                KilledMonsters = monsters,
            });
        }

        return snapshots;
    }

    /// <summary>
    /// Splits a session at the given timestamp. Snapshots at or after splitAt are moved
    /// to a new session. The old session is ended at splitAt, and the new session starts at splitAt.
    /// </summary>
    public void SplitSession(string oldSessionId, string newSessionId, string characterName,
        DateTime splitAt, DateTime oldSessionStart, SessionBaseline? baseline = null)
    {
        using var transaction = _connection.BeginTransaction();

        // Create the new session record
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO hunt_sessions (id, character_name, started_at_utc, active_duration_seconds, is_synced)
                VALUES (@id, @name, @started, 0, 0)
                """;
            cmd.Parameters.AddWithValue("@id", newSessionId);
            cmd.Parameters.AddWithValue("@name", characterName);
            cmd.Parameters.AddWithValue("@started", splitAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        // Move snapshots at or after splitAt to the new session,
        // rebasing session_time_seconds relative to the new start time
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE hunt_snapshots
                SET session_id = @newSessionId,
                    session_time_seconds = CAST(
                        (julianday(timestamp_utc) - julianday(@splitAt)) * 86400 AS INTEGER)
                WHERE session_id = @oldSessionId
                  AND timestamp_utc >= @splitAt
                """;
            cmd.Parameters.AddWithValue("@newSessionId", newSessionId);
            cmd.Parameters.AddWithValue("@oldSessionId", oldSessionId);
            cmd.Parameters.AddWithValue("@splitAt", splitAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        // End the old session at the split point
        var oldDuration = (int)(splitAt - oldSessionStart).TotalSeconds;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE hunt_sessions
                SET ended_at_utc = @ended, end_reason = @reason, active_duration_seconds = @duration,
                    baseline_xp_gain = @baselineXpGain, baseline_loot = @baselineLoot,
                    baseline_supplies = @baselineSupplies, baseline_balance = @baselineBalance,
                    baseline_damage = @baselineDamage, baseline_healing = @baselineHealing,
                    baseline_killed_monsters = @baselineMonsters,
                    updated_at = datetime('now')
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", oldSessionId);
            cmd.Parameters.AddWithValue("@ended", splitAt.ToString("o"));
            cmd.Parameters.AddWithValue("@reason", "MobSetChanged");
            AddBaselineParameters(cmd, baseline);
            cmd.Parameters.AddWithValue("@duration", oldDuration);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int GetUnsyncedCompletedSessionCount()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM hunt_sessions
            WHERE ended_at_utc IS NOT NULL AND is_synced = 0
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void SetBaseline(string sessionId, SessionBaseline baseline)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE hunt_sessions
            SET baseline_xp_gain = @baselineXpGain, baseline_loot = @baselineLoot,
                baseline_supplies = @baselineSupplies, baseline_balance = @baselineBalance,
                baseline_damage = @baselineDamage, baseline_healing = @baselineHealing,
                baseline_killed_monsters = @baselineMonsters
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        AddBaselineParameters(cmd, baseline);
        cmd.ExecuteNonQuery();
    }

    public void MarkSynced(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE hunt_sessions SET is_synced = 1, updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static void AddBaselineParameters(SqliteCommand cmd, SessionBaseline? baseline)
    {
        cmd.Parameters.AddWithValue("@baselineXpGain", (object?)baseline?.XpGain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineLoot", (object?)baseline?.Loot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineSupplies", (object?)baseline?.Supplies ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineBalance", (object?)baseline?.Balance ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineDamage", (object?)baseline?.Damage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineHealing", (object?)baseline?.Healing ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baselineMonsters",
            baseline?.KilledMonsters.Count > 0
                ? JsonSerializer.Serialize(baseline.KilledMonsters)
                : DBNull.Value);
    }

    private static SessionBaseline? ReadBaseline(SqliteDataReader reader, int startColumn)
    {
        int xpCol = startColumn, lootCol = startColumn + 1, supCol = startColumn + 2,
            balCol = startColumn + 3, dmgCol = startColumn + 4, healCol = startColumn + 5,
            monCol = startColumn + 6;

        if (reader.IsDBNull(xpCol) && reader.IsDBNull(lootCol) && reader.IsDBNull(supCol)
            && reader.IsDBNull(balCol) && reader.IsDBNull(dmgCol) && reader.IsDBNull(healCol)
            && reader.IsDBNull(monCol))
            return null;

        var monsters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!reader.IsDBNull(monCol))
        {
            var json = reader.GetString(monCol);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (parsed != null)
                monsters = new Dictionary<string, int>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        return new SessionBaseline
        {
            XpGain = reader.IsDBNull(xpCol) ? null : reader.GetInt64(xpCol),
            Loot = reader.IsDBNull(lootCol) ? null : reader.GetInt64(lootCol),
            Supplies = reader.IsDBNull(supCol) ? null : reader.GetInt64(supCol),
            Balance = reader.IsDBNull(balCol) ? null : reader.GetInt64(balCol),
            Damage = reader.IsDBNull(dmgCol) ? null : reader.GetInt64(dmgCol),
            Healing = reader.IsDBNull(healCol) ? null : reader.GetInt64(healCol),
            KilledMonsters = monsters,
        };
    }

    /// <summary>
    /// Returns distinct dates (descending) where a session either started or ended.
    /// Used by the logging system to retain logs only for recent play days.
    /// </summary>
    public List<DateOnly> GetHuntDates()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT d FROM (
                SELECT DATE(started_at_utc) as d FROM hunt_sessions WHERE ended_at_utc IS NOT NULL
                UNION
                SELECT DATE(ended_at_utc) as d FROM hunt_sessions WHERE ended_at_utc IS NOT NULL
            ) ORDER BY d DESC
            """;

        var dates = new List<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (DateOnly.TryParse(reader.GetString(0), out var date))
                dates.Add(date);
        }
        return dates;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ── Screenshot methods ──────────────────────────────────────────────

    public long InsertScreenshot(string sessionId, int sessionTimeSeconds, long? rawXpPerHour, string filePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_screenshots (session_id, session_time_seconds, raw_xp_per_hour, file_path)
            VALUES (@sessionId, @sessionTime, @rawXpPerHour, @filePath);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@sessionTime", sessionTimeSeconds);
        cmd.Parameters.AddWithValue("@rawXpPerHour", (object?)rawXpPerHour ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filePath", filePath);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ScreenshotRecord> GetScreenshots(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_time_seconds, raw_xp_per_hour, file_path, is_uploaded
            FROM session_screenshots
            WHERE session_id = @sessionId
            ORDER BY session_time_seconds ASC
            """;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var list = new List<ScreenshotRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScreenshotRecord(
                Id: reader.GetInt64(0),
                SessionTimeSeconds: reader.GetInt32(1),
                RawXpPerHour: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                FilePath: reader.GetString(3),
                IsUploaded: reader.GetInt32(4) == 1));
        }
        return list;
    }

    public List<ScreenshotRecord> GetUnuploadedScreenshots()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_time_seconds, raw_xp_per_hour, file_path, is_uploaded
            FROM session_screenshots
            WHERE is_uploaded = 0
            ORDER BY session_time_seconds ASC
            """;

        var list = new List<ScreenshotRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScreenshotRecord(
                Id: reader.GetInt64(0),
                SessionTimeSeconds: reader.GetInt32(1),
                RawXpPerHour: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                FilePath: reader.GetString(3),
                IsUploaded: reader.GetInt32(4) == 1));
        }
        return list;
    }

    public void DeleteScreenshotsByIds(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;

        using var cmd = _connection.CreateCommand();
        var paramNames = ids.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"DELETE FROM session_screenshots WHERE id IN ({string.Join(",", paramNames)})";
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
        cmd.ExecuteNonQuery();
    }

    public void DeleteScreenshotsBySession(string sessionId, IReadOnlyList<long> excludeIds)
    {
        using var cmd = _connection.CreateCommand();
        if (excludeIds.Count == 0)
        {
            cmd.CommandText = "DELETE FROM session_screenshots WHERE session_id = @sessionId";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
        }
        else
        {
            var paramNames = excludeIds.Select((_, i) => $"@eid{i}").ToList();
            cmd.CommandText = $"DELETE FROM session_screenshots WHERE session_id = @sessionId AND id NOT IN ({string.Join(",", paramNames)})";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            for (int i = 0; i < excludeIds.Count; i++)
                cmd.Parameters.AddWithValue(paramNames[i], excludeIds[i]);
        }
        cmd.ExecuteNonQuery();
    }

    public void MarkScreenshotsUploaded(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;

        using var cmd = _connection.CreateCommand();
        var paramNames = ids.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"UPDATE session_screenshots SET is_uploaded = 1 WHERE id IN ({string.Join(",", paramNames)})";
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
        cmd.ExecuteNonQuery();
    }

}
