using Catena.Contracts;

namespace Catena.Core;

public sealed record PaneState
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Location Location { get; init; }
    public Location? PinnedRoot { get; set; }
    public List<Location> History { get; init; } = [];
    public int HistoryIndex { get; init; } = -1;
    public string Filter { get; init; } = "";
    public int Sort { get; init; }
    public bool TreeVisible { get; init; }
    public double TreeWidth { get; init; } = 176;
    public double ListZoom { get; init; } = 1;
    public List<Location> SelectedLocations { get; init; } = [];
}

public sealed record Workspace
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "默认工作区";
    public int PaneCount { get; set; } = 2;
    public bool TwoPaneRows { get; set; }
    public double HorizontalRatio { get; set; } = .5;
    public double VerticalRatio { get; set; } = .5;
    public double PreviewWidth { get; set; } = 380;
    public Guid ActivePaneId { get; set; }
    public List<PaneState> Panes { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("此工作区版本不受支持，原数据已保留。");
        if (Panes is null || PaneCount is not (1 or 2 or 4) || Panes.Count < PaneCount || Panes.Count > 4 ||
            Panes.Any(p => p is null || !ValidLocation(p.Location) || (p.PinnedRoot is not null && !ValidLocation(p.PinnedRoot)) ||
                p.History is null || p.History.Any(h => !ValidLocation(h)) || p.SelectedLocations is null || p.Filter is null ||
                !double.IsFinite(p.TreeWidth) || p.TreeWidth is < 100 or > 1000 ||
                !double.IsFinite(p.ListZoom) || p.ListZoom is < .75 or > 2) ||
            Panes.Select(p => p.Id).Distinct().Count() != Panes.Count || string.IsNullOrWhiteSpace(Name) ||
            !Panes.Any(p => p.Id == ActivePaneId) ||
            !double.IsFinite(HorizontalRatio) || !double.IsFinite(VerticalRatio) ||
            HorizontalRatio is <= 0 or >= 1 || VerticalRatio is <= 0 or >= 1 ||
            !double.IsFinite(PreviewWidth) || PreviewWidth is < 200 or > 10000)
            throw new InvalidDataException("工作区状态无效，原数据已保留。");
    }
    private static bool ValidLocation(Location? location) => location is not null && location.ProviderId == "local" &&
        Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) && uri.IsFile;

    public static Workspace Create(Location home)
    {
        var panes = Enumerable.Range(0, 4).Select(_ => new PaneState { Location = home }).ToList();
        return new Workspace { Panes = panes, ActivePaneId = panes[0].Id };
    }
}

public sealed record WorkspaceSummary(Guid Id, string Name);
public interface IWorkspaceRepository
{
    Task InitializeAsync(CancellationToken token = default);
    Task<IReadOnlyList<WorkspaceSummary>> ListAsync(CancellationToken token = default);
    Task<Workspace?> LoadAsync(Guid? id = null, CancellationToken token = default);
    Task SaveAsync(Workspace workspace, CancellationToken token = default);
    Task DeleteAsync(Guid id, CancellationToken token = default);
}
