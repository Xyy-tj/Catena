using Catena.Core;
using Catena.Persistence;
using Catena.Storage.Local;
using Microsoft.Data.Sqlite;

namespace Catena.Tests;
public sealed class PersistenceTests
{
    [Fact] public void ExistingWorkspacesUseDefaultPreviewWidthAndInvalidWidthsAreRejected()
    {
        using var temp = new TestDirectory();
        var workspace = Workspace.Create(new LocalFileSystemProvider().Normalize(temp.Path));
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(workspace))!.AsObject();
        json.Remove("PreviewWidth");
        var restored = System.Text.Json.JsonSerializer.Deserialize<Workspace>(json.ToJsonString())!;
        restored.Validate(); Assert.Equal(380, restored.PreviewWidth);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, 0, -1, 10001 })
        { restored.PreviewWidth = value; Assert.Throws<InvalidDataException>(restored.Validate); }
    }

    [Fact] public async Task WorkspacesRoundTripHiddenPanesAndLastActiveId()
    {
        using var temp = new TestDirectory(); var db = Path.Combine(temp.Path, "state.db");
        var repository = new SqliteWorkspaceRepository(db); await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var location = new LocalFileSystemProvider().Normalize(temp.Path);
        var first = Workspace.Create(location); first.Name = "科研"; first.PaneCount = 1;
        first.Panes[3].PinnedRoot = location;
        await repository.SaveAsync(first, TestContext.Current.CancellationToken);
        var second = Workspace.Create(location); second.Name = "开发"; await repository.SaveAsync(second, TestContext.Current.CancellationToken);
        var reopened = new SqliteWorkspaceRepository(db); await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(second.Id, (await reopened.LoadAsync(token: TestContext.Current.CancellationToken))!.Id);
        var loaded = (await reopened.LoadAsync(first.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(1, loaded.PaneCount); Assert.Equal(4, loaded.Panes.Count); Assert.Equal(location, loaded.Panes[3].PinnedRoot);
        await reopened.DeleteAsync(second.Id, TestContext.Current.CancellationToken); Assert.Single(await reopened.ListAsync(TestContext.Current.CancellationToken)); Assert.Null(await reopened.LoadAsync(second.Id, TestContext.Current.CancellationToken));
    }
    [Fact] public async Task NewerDatabaseVersionIsNotOverwritten()
    {
        using var temp = new TestDirectory(); var db = Path.Combine(temp.Path, "future.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(db);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteWorkspaceRepository(db).InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(db));
    }
    [Fact] public async Task CorruptDatabaseIsPreserved()
    {
        using var temp = new TestDirectory(); var db = Path.Combine(temp.Path, "broken.db");
        File.WriteAllText(db, "not a database"); var before = File.ReadAllBytes(db);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteWorkspaceRepository(db).InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(db));
    }
}

