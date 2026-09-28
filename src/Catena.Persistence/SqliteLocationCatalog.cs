using System.Text.Json;
using Catena.Contracts;
using Microsoft.Data.Sqlite;

namespace Catena.Persistence;

public sealed class SqliteLocationCatalog(string databasePath) : ILocationCatalogStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<T> Execute<T>(Func<SqliteConnection, T> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
                db.Open(); return action(db);
            }, token);
        }
        finally { gate.Release(); }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public Task InitializeAsync(IReadOnlyList<string> defaults, CancellationToken token = default) => Execute(db =>
    {
        using var version = Command(db, "PRAGMA user_version");
        if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new InvalidDataException("位置缓存版本不受支持。");
        using var transaction = db.BeginTransaction();
        using var schema = Command(db, """
            CREATE TABLE IF NOT EXISTS roots(path TEXT PRIMARY KEY COLLATE NOCASE,name TEXT NOT NULL,profile TEXT);
            CREATE TABLE IF NOT EXISTS entries(root TEXT COLLATE NOCASE,path TEXT COLLATE NOCASE,parent TEXT COLLATE NOCASE,data TEXT NOT NULL,is_dir INTEGER NOT NULL,PRIMARY KEY(root,path));
            CREATE INDEX IF NOT EXISTS entry_parent ON entries(parent,is_dir);
            CREATE TABLE IF NOT EXISTS folders(path TEXT PRIMARY KEY COLLATE NOCASE,data TEXT NOT NULL,updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS recent(path TEXT PRIMARY KEY COLLATE NOCASE,name TEXT NOT NULL,is_file INTEGER NOT NULL,visited INTEGER NOT NULL,visits INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);
            PRAGMA user_version=1;
            """); schema.Transaction = transaction; schema.ExecuteNonQuery();
        using var seeded = Command(db, "SELECT COUNT(*) FROM meta WHERE key='seeded'"); seeded.Transaction = transaction;
        if (Convert.ToInt32(seeded.ExecuteScalar()) == 0)
        {
            foreach (var path in defaults.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
            {
                using var add = Command(db, "INSERT OR IGNORE INTO roots(path,name) VALUES($p,$n)", ("$p", path), ("$n", Path.GetFileName(path)));
                add.Transaction = transaction; add.ExecuteNonQuery();
            }
            using var mark = Command(db, "INSERT INTO meta VALUES('seeded','1')"); mark.Transaction = transaction; mark.ExecuteNonQuery();
        }
        transaction.Commit(); return true;
    }, token);

    public Task<IReadOnlyList<CatalogRoot>> GetRootsAsync(CancellationToken token = default) => Execute<IReadOnlyList<CatalogRoot>>(db =>
    {
        using var command = Command(db, "SELECT path,name,profile FROM roots ORDER BY name"); using var rows = command.ExecuteReader(); var result = new List<CatalogRoot>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.IsDBNull(2) ? null : JsonSerializer.Deserialize<StructureProfile>(rows.GetString(2))));
        return result;
    }, token);
    public Task AddRootAsync(string path, string name, CancellationToken token = default) => Execute(db =>
    {
        using var count = Command(db, "SELECT COUNT(*) FROM roots");
        if (Convert.ToInt32(count.ExecuteScalar()) >= 20) throw new InvalidOperationException("最多保留 20 个常用位置。");
        using var add = Command(db, "INSERT OR IGNORE INTO roots(path,name) VALUES($p,$n)", ("$p", path), ("$n", name)); return add.ExecuteNonQuery();
    }, token);
    public Task RemoveRootAsync(string path, CancellationToken token = default) => Execute(db =>
    {
        using var tx = db.BeginTransaction();
        using var remove = Command(db, "DELETE FROM entries WHERE root=$p; DELETE FROM roots WHERE path=$p; DELETE FROM folders WHERE path=$p OR substr(path,1,length($prefix))=$prefix COLLATE NOCASE;",
            ("$p", path), ("$prefix", Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar)); remove.Transaction = tx;
        remove.ExecuteNonQuery(); tx.Commit(); return true;
    }, token);
    public Task SaveSnapshotAsync(CatalogSnapshot snapshot, CancellationToken token = default) => Execute(db =>
    {
        using var tx = db.BeginTransaction();
        using var update = Command(db, "UPDATE roots SET profile=$data WHERE path=$p", ("$data", JsonSerializer.Serialize(snapshot.Profile)), ("$p", snapshot.Root)); update.Transaction = tx;
        if (update.ExecuteNonQuery() == 0) return false; // A removed root cannot be resurrected by a finishing scan.
        if (!snapshot.Profile.Partial)
        { using var delete = Command(db, "DELETE FROM entries WHERE root=$p", ("$p", snapshot.Root)); delete.Transaction = tx; delete.ExecuteNonQuery(); }
        using var insert = Command(db, "INSERT OR REPLACE INTO entries(root,path,parent,data,is_dir) VALUES($r,$p,$parent,$data,$dir)",
            ("$r", snapshot.Root), ("$p", ""), ("$parent", ""), ("$data", ""), ("$dir", 0)); insert.Transaction = tx; insert.Prepare();
        foreach (var item in snapshot.Items)
        {
            token.ThrowIfCancellationRequested();
            insert.Parameters["$p"].Value = item.Path; insert.Parameters["$parent"].Value = item.Parent;
            insert.Parameters["$data"].Value = JsonSerializer.Serialize(item); insert.Parameters["$dir"].Value = item.IsDirectory ? 1 : 0; insert.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); tx.Commit(); return true;
    }, token);
    public Task<FolderListing?> GetFoldersAsync(string path, CancellationToken token = default) => Execute(db =>
    {
        FolderListing? cached = null;
        using (var command = Command(db, "SELECT data FROM folders WHERE path=$p", ("$p", path)))
            if (command.ExecuteScalar() is string json) cached = JsonSerializer.Deserialize<FolderListing>(json);
        using var root = Command(db, "SELECT path,profile FROM roots WHERE (path=$p OR EXISTS(SELECT 1 FROM entries WHERE root=roots.path AND path=$p AND is_dir=1)) AND profile IS NOT NULL", ("$p", path));
        using var profiles = root.ExecuteReader(); StructureProfile? profile = null; string? owner = null;
        while (profiles.Read())
        { var value = JsonSerializer.Deserialize<StructureProfile>(profiles.GetString(1)); if (value?.Updated > (profile?.Updated ?? DateTimeOffset.MinValue)) { profile = value; owner = profiles.GetString(0); } }
        profiles.Close();
        if (profile is null || cached?.Updated > profile.Updated) return cached;
        using var entries = Command(db, "SELECT path,data FROM entries WHERE root=$root AND parent=$p AND is_dir=1 ORDER BY path LIMIT 2001", ("$p", path), ("$root", owner));
        using var rows = entries.ExecuteReader(); var items = new List<CatalogItem>();
        while (rows.Read()) { token.ThrowIfCancellationRequested(); items.Add(JsonSerializer.Deserialize<CatalogItem>(rows.GetString(1))!); }
        return new FolderListing(items.Take(2000).ToArray(), profile.Updated, items.Count > 2000 || profile.Partial);
    }, token);
    public Task SaveFoldersAsync(string path, FolderListing listing, CancellationToken token = default) => Execute(db =>
    {
        using var command = Command(db, """
            INSERT OR REPLACE INTO folders(path,data,updated) VALUES($p,$data,$time);
            DELETE FROM folders WHERE path NOT IN(SELECT path FROM folders ORDER BY updated DESC LIMIT 100);
            """, ("$p", path), ("$data", JsonSerializer.Serialize(listing)), ("$time", listing.Updated.ToUnixTimeMilliseconds())); return command.ExecuteNonQuery();
    }, token);
    public Task RecordVisitAsync(string path, bool isFile, CancellationToken token = default) => Execute(db =>
    {
        using var command = Command(db, """
            INSERT INTO recent(path,name,is_file,visited,visits) VALUES($p,$n,$file,$time,1)
            ON CONFLICT(path) DO UPDATE SET visited=$time,visits=visits+1,is_file=$file;
            DELETE FROM recent WHERE visited<$cutoff OR path NOT IN(SELECT path FROM recent ORDER BY visited DESC LIMIT 300);
            """, ("$p", path), ("$n", Path.GetFileName(path) is { Length: > 0 } name ? name : path), ("$file", isFile ? 1 : 0),
            ("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds())); return command.ExecuteNonQuery();
    }, token);
    public Task<IReadOnlyList<RecentLocation>> GetRecentAsync(CancellationToken token = default) => Execute<IReadOnlyList<RecentLocation>>(db =>
    {
        using var command = Command(db, "SELECT path,name,is_file,visited,visits FROM recent WHERE visited>=$cutoff ORDER BY visited DESC LIMIT 30", ("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds()));
        using var rows = command.ExecuteReader(); var items = new List<RecentLocation>();
        while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2), DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(3)), rows.GetInt32(4)));
        return items;
    }, token);
    public Task ClearRecentAsync(CancellationToken token = default) => Execute(db => { using var command = Command(db, "DELETE FROM recent"); return command.ExecuteNonQuery(); }, token);
}
