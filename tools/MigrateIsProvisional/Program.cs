using Microsoft.Data.Sqlite;

// Walk up from cwd looking for data/ClassicalCanon.db
var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
string? dbPath = null;
while (dir != null)
{
    var candidate = Path.Combine(dir.FullName, "data", "ClassicalCanon.db");
    if (File.Exists(candidate)) { dbPath = candidate; break; }
    dir = dir.Parent;
}
if (dbPath is null)
{
    Console.WriteLine("ERROR: could not find data/ClassicalCanon.db");
    return 1;
}

Console.WriteLine($"DB: {dbPath}");
var beforeMtime = File.GetLastWriteTime(dbPath);
Console.WriteLine($"Before mtime: {beforeMtime:O}");

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

foreach (var table in new[] { "composers", "pieces" })
{
    bool exists = false;
    using (var check = conn.CreateCommand())
    {
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "is_provisional", StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }
    }

    if (exists)
    {
        Console.WriteLine($"[{table}] is_provisional already present");
        using var fix = conn.CreateCommand();
        fix.CommandText = $"UPDATE {table} SET is_provisional = 1 WHERE is_provisional = 0";
        var rows = fix.ExecuteNonQuery();
        Console.WriteLine($"[{table}] flipped {rows} rows from 0 -> 1 (back-fill from buggy DEFAULT 0 migration)");
    }
    else
    {
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN is_provisional INTEGER NOT NULL DEFAULT 1";
        alter.ExecuteNonQuery();
        Console.WriteLine($"[{table}] added is_provisional INTEGER NOT NULL DEFAULT 1");
    }

    using var count = conn.CreateCommand();
    count.CommandText = $"SELECT COUNT(*), SUM(is_provisional) FROM {table}";
    using var reader2 = count.ExecuteReader();
    if (reader2.Read())
    {
        var total = reader2.GetInt64(0);
        var sum = reader2.IsDBNull(1) ? 0 : reader2.GetInt64(1);
        Console.WriteLine($"[{table}] total rows: {total}, provisional rows: {sum}");
    }
}

conn.Close();

var afterMtime = File.GetLastWriteTime(dbPath);
Console.WriteLine($"After mtime: {afterMtime:O}");
Console.WriteLine($"Mtime changed: {beforeMtime != afterMtime}");
return 0;
