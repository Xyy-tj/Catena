using System.Text.Json;
using Catena.Core;
using Microsoft.Data.Sqlite;

namespace Catena.Persistence;

public sealed class SqliteWorkspaceRepository(string databasePath) : IWorkspaceRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                connection.Open();
                return action(connection);
            }, token);
        }
        finally { gate.Release(); }
    }

    public Task InitializeAsync(CancellationToken token = default) => ExecuteAsync(connection =>
    {
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 1) throw new InvalidDataException("数据库来自更新版本，请使用对应版本打开。原库未修改。");
        if (current == 0)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY, name TEXT NOT NULL, state TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS session(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery(); transaction.Commit();
        }
        return true;
    }, token);

    public Task<IReadOnlyList<WorkspaceSummary>> ListAsync(CancellationToken token = default) => ExecuteAsync<IReadOnlyList<WorkspaceSummary>>(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT id, name FROM workspaces ORDER BY name";
        using var reader = command.ExecuteReader(); var list = new List<WorkspaceSummary>();
        while (reader.Read()) list.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return list;
    }, token);

    public Task<Workspace?> LoadAsync(Guid? id = null, CancellationToken token = default) => ExecuteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = id.HasValue ? "SELECT state FROM workspaces WHERE id = $id" :
            "SELECT state FROM workspaces WHERE id = (SELECT value FROM session WHERE key = 'active')";
        if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value.ToString());
        var json = command.ExecuteScalar() as string;
        if (json is null) return null;
        var workspace = JsonSerializer.Deserialize<Workspace>(json, JsonOptions) ?? throw new InvalidDataException("工作区数据为空。");
        workspace.Validate(); return workspace;
    }, token);

    public Task SaveAsync(Workspace workspace, CancellationToken token = default)
    {
        workspace.Validate();
        // Freeze state before crossing the async boundary.
        var id = workspace.Id.ToString(); var name = workspace.Name; var json = JsonSerializer.Serialize(workspace, JsonOptions);
        return ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO workspaces(id,name,state) VALUES($id,$name,$state)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,state=excluded.state;
                INSERT INTO session(key,value) VALUES('active',$id) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$state", json); command.ExecuteNonQuery(); transaction.Commit(); return true;
        }, token);
    }

    public Task DeleteAsync(Guid id, CancellationToken token = default) => ExecuteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM workspaces WHERE id=$id; DELETE FROM session WHERE key='active' AND value=$id;";
        command.Parameters.AddWithValue("$id", id.ToString()); command.ExecuteNonQuery(); transaction.Commit(); return true;
    }, token);
}
