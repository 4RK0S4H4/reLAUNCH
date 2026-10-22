using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReLaunch.Core;
using ReLaunch.Core.Catalogue;

namespace ReLaunch.Service;

public sealed record ActivityRow(DateTimeOffset Time, string DeviceId, string AppId, string Action, string Result);

/// <summary>SQLite store in %ProgramData%\reLAUNCH: favorites, overrides and the activity log (phone devices live in the relay).</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DataPaths.Database }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS favorites (appId TEXT PRIMARY KEY, position INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS overrides (appId TEXT PRIMARY KEY, args TEXT, extraProcesses TEXT NOT NULL DEFAULT '[]', closeTimeoutMs INTEGER);
            CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, deviceId TEXT NOT NULL, appId TEXT NOT NULL, action TEXT NOT NULL, result TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, AppOverride> GetOverrides()
    {
        var result = new Dictionary<string, AppOverride>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT appId, args, extraProcesses, closeTimeoutMs FROM overrides";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var extras = JsonSerializer.Deserialize<string[]>(r.GetString(2)) ?? [];
            result[r.GetString(0)] = new AppOverride(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                extras,
                r.IsDBNull(3) ? null : r.GetInt32(3));
        }
        return result;
    }

    public void SetOverride(AppOverride o)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO overrides (appId, args, extraProcesses, closeTimeoutMs) VALUES ($id, $args, $extra, $timeout)
            ON CONFLICT(appId) DO UPDATE SET args = $args, extraProcesses = $extra, closeTimeoutMs = $timeout
            """;
        cmd.Parameters.AddWithValue("$id", o.AppId);
        cmd.Parameters.AddWithValue("$args", (object?)o.Args ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$extra", JsonSerializer.Serialize(o.ExtraProcesses));
        cmd.Parameters.AddWithValue("$timeout", (object?)o.CloseTimeoutMs ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetFavorites()
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT appId FROM favorites ORDER BY position";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void SetFavorites(IEnumerable<string> orderedIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM favorites";
            clear.ExecuteNonQuery();
        }
        var position = 0;
        foreach (var id in orderedIds.Distinct())
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO favorites (appId, position) VALUES ($id, $pos)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$pos", position++);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AddActivity(string deviceId, string appId, string action, string result)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO activity (ts, deviceId, appId, action, result) VALUES ($ts, $d, $a, $act, $res)";
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$a", appId);
        cmd.Parameters.AddWithValue("$act", action);
        cmd.Parameters.AddWithValue("$res", result);
        cmd.ExecuteNonQuery();
    }

    public List<ActivityRow> GetActivity(int limit)
    {
        var list = new List<ActivityRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, deviceId, appId, action, result FROM activity ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ActivityRow(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }
}
