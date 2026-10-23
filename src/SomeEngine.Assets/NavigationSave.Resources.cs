using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

/// <summary>Asset provenance owned by the game while runtime components hold ordinary navigation objects.</summary>
public sealed partial class NavigationSaveResources
{
    internal Dictionary<object, NavigationSavedResource> Sources { get; } = new(ReferenceEqualityComparer.Instance);

    public void Register(NavigationProfile value, AssetId<NavigationProfileAsset> asset) => Sources[value] = new SavedProfile(asset);
    public void Register(NavigationPolicy value, AssetId<NavigationPolicyAsset> asset) => Sources[value] = new SavedPolicy(asset);
    public void Register(NavigationMesh value, AssetId<NavigationAsset> asset, NavigationMeshSelection selection) => Sources[value] = new SavedMesh(asset, selection);
    public void Register(HeadingNavigationMesh value, AssetId<NavigationAsset> asset, NavigationMeshSelection selection) => Sources[value] = new SavedHeadingMesh(asset, selection);
    public void Register(NavigationGrid value, AssetId<NavigationAsset> asset, NavigationGridSelection selection) => Sources[GetGridSource(value)] = new SavedGrid(asset, selection);
    public void Register(NavigationMotionSet value, AssetId<NavigationProfileAsset> asset) => Sources[value] = new SavedMotions(asset);
    public void Register(NavigationGeometry value, AssetId<MeshAsset> asset) => Sources[value] = new SavedGeometry(asset);
    public void Register(NavigationLinkSet value, AssetId<NavigationAsset> asset) => Sources[value] = new SavedLinks(asset);
    public void Register(NavigationFlow value, AssetId<NavigationFlowAsset> asset) => Sources[value] = new SavedFlowAsset(asset, false);

    internal static object GetGridSource(NavigationGrid grid) => (object?)grid.RegionGraph ?? grid;
}

internal abstract record NavigationSavedResource;
internal sealed record SavedProfile(AssetId<NavigationProfileAsset> Asset) : NavigationSavedResource;
internal sealed record SavedPolicy(AssetId<NavigationPolicyAsset> Asset) : NavigationSavedResource;
internal sealed record SavedMesh(AssetId<NavigationAsset> Asset, NavigationMeshSelection Selection) : NavigationSavedResource;
internal sealed record SavedHeadingMesh(AssetId<NavigationAsset> Asset, NavigationMeshSelection Selection) : NavigationSavedResource;
internal sealed record SavedGrid(AssetId<NavigationAsset> Asset, NavigationGridSelection Selection) : NavigationSavedResource;
internal sealed record SavedMotions(AssetId<NavigationProfileAsset> Asset) : NavigationSavedResource;
internal sealed record SavedGeometry(AssetId<MeshAsset> Asset) : NavigationSavedResource;
internal sealed record SavedLinks(AssetId<NavigationAsset> Asset, NavigationLink[]? Definitions = null) : NavigationSavedResource;
internal sealed record SavedFlowAsset(AssetId<NavigationFlowAsset> Asset, bool Heading) : NavigationSavedResource;
internal readonly record struct SavedCell(Vector3 Position, int Layer);
internal readonly record struct SavedCellCost(SavedCell Cell, float Cost);
internal readonly record struct SavedEdgeCost(SavedCell From, SavedCell To, int Action, ulong Capabilities, float Cost);
internal sealed record SavedCosts(int Grid, int Policy, ulong Version, SavedCellCost[] Cells, SavedEdgeCost[] Edges) : NavigationSavedResource;
internal sealed record SavedVehicleSpace(int Grid, int Motions, int Costs, HybridAStarSettings Settings) : NavigationSavedResource;
internal sealed record SavedFlow(int Costs, int Profile, NavigationFlowGoal[] Goals, ulong GoalVersion, ulong Version,
    string Solver, SavedFlowAsset? Source, int HeadingGraph = -1) : NavigationSavedResource;

internal sealed partial class NavigationSaveResourceCapture(NavigationSaveResources sources)
{
    private readonly Dictionary<object, int> _indices = new(ReferenceEqualityComparer.Instance);
    internal List<NavigationSavedResource> Records { get; } = [];

    internal int Add(object? value)
    {
        if (value is null) return -1;
        if (value is NavigationCosts costs) value = costs.Source ?? costs;
        object source = value is NavigationGrid grid ? NavigationSaveResources.GetGridSource(grid) : value;
        if (_indices.TryGetValue(source, out int known)) return known;
        int index = Records.Count;
        _indices.Add(source, index);
        Records.Add(null!);
        Records[index] = _derived.TryGetValue(source, out NavigationSavedResource? derived) ? derived : value switch
        {
            NavigationCosts values => CaptureCosts(values),
            NavigationVehicleSpace vehicle => new SavedVehicleSpace(Add(vehicle.Grid), Add(vehicle.Motions), Add(vehicle.Costs), vehicle.HybridSettings),
            NavigationFlow flow => CaptureFlow(flow),
            NavigationScene scene => CaptureScene(scene),
            HeadingNavigationScene scene => new SavedHeadingScene(Add(((HeadingSceneSaveSource)sources.Sources[scene]).Mesh)),
            NavigationLinkSet links => new SavedLinks(sources.Sources.TryGetValue(links, out NavigationSavedResource? origin)
                ? ((SavedLinks)origin).Asset : default, links.Links.ToArray()),
            _ => sources.Sources[source],
        };
        return index;
    }

    private SavedFlow CaptureFlow(NavigationFlow flow)
    {
        NavigationFlowInput input = flow.Input;
        sources.Sources.TryGetValue(flow, out NavigationSavedResource? source);
        int heading = input.HeadingGraph is { } graph ? CaptureHeadingGraph(graph, input.Profile) : -1;
        return new(Add(input.Costs), Add(input.Profile), input.Goals.ToArray(), input.GoalVersion, input.Version,
            flow.Field?.Solver ?? ReverseDijkstraFlow.Id, source as SavedFlowAsset, heading);
    }

    private SavedCosts CaptureCosts(NavigationCosts costs)
    {
        NavigationGrid grid = costs.Grid;
        var cells = new SavedCellCost[grid.Cells.Length];
        var edges = new List<SavedEdgeCost>(grid.Edges.Length);
        for (int cell = 0; cell < grid.Cells.Length; cell++)
        {
            NavigationGridCell from = grid.Cells.Span[cell];
            var key = new SavedCell(from.Position, from.Layer);
            cells[cell] = new(key, costs.CellCosts.Span[cell]);
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                NavigationGridEdge transition = grid.Edges.Span[edge];
                NavigationGridCell to = grid.Cells.Span[transition.ToCell];
                edges.Add(new(key, new(to.Position, to.Layer), transition.Action, transition.RequiredCapabilities, costs.EdgeCosts.Span[edge]));
            }
        }
        return new(Add(grid), Add(costs.Policy), costs.Version, cells, edges.ToArray());
    }
}
