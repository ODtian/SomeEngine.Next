using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

public static partial class NavigationSave
{
    public static async ValueTask<NavigationSaveResult> ReadAsync(Stream stream, SerializationRegistry gameRegistry,
        AssetLoader loader, Scheduler? scheduler = null, NavigationSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        scheduler ??= JobSystem.Default;
        NavigationSavedTable table = WorldSerializer.ReadDurableComponent<NavigationSavedTable>(stream, TableRegistry);
        World world = WorldSerializer.ReadDurableWorld(stream, gameRegistry, new(Execution: new WorldJobs(scheduler)));
        try
        {
            var resources = new NavigationSaveResources();
            var reader = new NavigationSaveResourceReader(table.Resources, resources, loader, scheduler, cancellationToken);
            await reader.RestoreSources(world, table, options ?? new()).ConfigureAwait(false);
            foreach (NavigationSavedActor actor in table.Actors)
            {
                var profile = (NavigationProfile)await reader.Get(actor.Profile).ConfigureAwait(false);
                var policy = (NavigationPolicy)await reader.Get(actor.Policy).ConfigureAwait(false);
                object? resource = actor.Resource < 0 ? null : await reader.Get(actor.Resource).ConfigureAwait(false);
                switch (actor.Kind)
                {
                    case SavedPathKind.None: world.Add(actor.Entity, new NavigationAgent(profile, policy, actor.DesiredSpeed)); break;
                    case SavedPathKind.Flow:
                        NavigationComponents.AddFlow(world, actor.Entity, (NavigationFlow)resource!, actor.DesiredSpeed);
                        NavigationComponents.RestoreFlowTraversal(world, actor.Entity, actor.Traversal, ((NavigationFlow)resource!).Input.HeadingGraph?.Links);
                        if (!actor.Enabled) world.Disable<FlowFollower>(actor.Entity);
                        break;
                    case SavedPathKind.Polygon: AddPath<NavigationMesh, PolygonSearchState>(); break;
                    case SavedPathKind.Polyanya: AddPath<NavigationMesh, PolyanyaSearchState>(); break;
                    case SavedPathKind.Asa: AddPath<HeadingNavigationMesh, AsaSearchState>(); break;
                    case SavedPathKind.StateLattice: AddPath<NavigationVehicleSpace, StateLatticeSearchState>(); break;
                    case SavedPathKind.HybridAStar: AddPath<NavigationVehicleSpace, HybridAStarSearchState>(); break;
                    case SavedPathKind.Scene: AddPath<NavigationScene, SceneSearchState>(); break;
                    case SavedPathKind.DStarLite: AddPath<NavigationScene, DStarLiteSearchState>(); break;
                    case SavedPathKind.HeadingScene: AddPath<HeadingNavigationScene, HeadingSceneSearchState>(); break;
                }
                await RestoreControls(world, actor.Entity, actor.Controls, reader).ConfigureAwait(false);
                void AddPath<TSpace, TState>() where TSpace : class, INavigationSpace<TSpace> where TState : class
                {
                    NavigationComponents.AddPath<TSpace, TState>(world, actor.Entity, (TSpace)resource!, profile, policy, actor.Goal, actor.DesiredSpeed);
                    NavigationComponents.RestorePathTraversal<TSpace, TState>(world, actor.Entity, actor.Traversal, (resource as NavigationScene)?.Content.Links);
                    if (!actor.Enabled) world.Disable<NavigationGoal>(actor.Entity);
                }
            }
            return new(world, resources);
        }
        catch { world.Dispose(); throw; }
    }
}

internal sealed partial class NavigationSaveResourceReader(NavigationSavedResource[] records, NavigationSaveResources restored,
    AssetLoader loader, Scheduler scheduler, CancellationToken cancellationToken)
{
    private readonly Task<object>?[] _loads = new Task<object>[records.Length];
    internal Task<object> Get(int index) => _loads[index] ??= Load(index);

    private async Task<object> Load(int index)
    {
        object result;
        NavigationSavedResource record = records[index];
        switch (record)
        {
            case SavedProfile value:
                using (NavigationProfileAsset asset = await NavigationProfileAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = (await asset.GetBlockAsync<NavigationProfileBlock>(cancellationToken).ConfigureAwait(false)).Create();
                break;
            case SavedPolicy value:
                using (NavigationPolicyAsset asset = await NavigationPolicyAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = (await asset.GetBlockAsync<NavigationPolicyBlock>(cancellationToken).ConfigureAwait(false)).Create();
                break;
            case SavedMesh value:
                using (NavigationAsset asset = await NavigationAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = await (await asset.GetBlockAsync<NavigationMeshBlock>(cancellationToken).ConfigureAwait(false))
                        .CreateAsync(value.Selection, cancellationToken).ConfigureAwait(false);
                break;
            case SavedHeadingMesh value:
                using (NavigationAsset asset = await NavigationAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = await (await asset.GetBlockAsync<HeadingNavigationMeshBlock>(cancellationToken).ConfigureAwait(false))
                        .CreateAsync(value.Selection, cancellationToken).ConfigureAwait(false);
                break;
            case SavedGrid value:
                using (NavigationAsset asset = await NavigationAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = await (await asset.GetBlockAsync<NavigationGridBlock>(cancellationToken).ConfigureAwait(false))
                        .CreateAsync(value.Selection, cancellationToken).ConfigureAwait(false);
                break;
            case SavedMotions value:
                using (NavigationProfileAsset asset = await NavigationProfileAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = await (await asset.GetBlockAsync<NavigationMotionBlock>(cancellationToken).ConfigureAwait(false)).CreateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SavedGeometry value:
                using (MeshAsset asset = await MeshAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false))
                    result = await (await asset.GetBlockAsync<NavigationGeometryBlock>(cancellationToken).ConfigureAwait(false)).CreateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SavedLinks value:
                if (value.Asset.Value != Guid.Empty)
                {
                    using NavigationAsset asset = await NavigationAsset.OpenAsync(loader, value.Asset, cancellationToken).ConfigureAwait(false);
                    (await asset.GetBlockAsync<NavigationLinkBlock>(cancellationToken).ConfigureAwait(false)).Create();
                }
                result = new NavigationLinkSet(value.Definitions!);
                break;
            case SavedScene value: return await RestoreScene(value).ConfigureAwait(false);
            case SavedHeadingScene value:
                var headingMesh = (HeadingNavigationMesh)await Get(value.Mesh).ConfigureAwait(false);
                var headingScene = new HeadingNavigationScene(new(headingMesh.RegionGraph!, headingMesh.Tiles));
                restored.Register(headingScene, headingMesh);
                return headingScene;
            case SavedBuiltGrid value: return _builtGrids[value.Region];
            case SavedHeadingGraph value: return await RestoreHeadingGraph(value).ConfigureAwait(false);
            case SavedHeadingCosts value: return ((HeadingNavigationFlowGraph)await Get(value.Graph).ConfigureAwait(false)).Costs;
            case SavedHeadingGrid value: return ((HeadingNavigationFlowGraph)await Get(value.Graph).ConfigureAwait(false)).Costs.Grid;
            case SavedCosts value:
                result = RestoreCosts(value, (NavigationGrid)await Get(value.Grid).ConfigureAwait(false),
                    (NavigationPolicy)await Get(value.Policy).ConfigureAwait(false));
                break;
            case SavedVehicleSpace value:
                result = new NavigationVehicleSpace((NavigationGrid)await Get(value.Grid).ConfigureAwait(false),
                    (NavigationMotionSet)await Get(value.Motions).ConfigureAwait(false),
                    value.Costs < 0 ? null : (NavigationCosts)await Get(value.Costs).ConfigureAwait(false), value.Settings);
                break;
            case SavedFlow value:
                var costs = (NavigationCosts)await Get(value.Costs).ConfigureAwait(false);
                var profile = (NavigationProfile)await Get(value.Profile).ConfigureAwait(false);
                HeadingNavigationFlowGraph? headingGraph = value.HeadingGraph < 0 ? null : (HeadingNavigationFlowGraph)await Get(value.HeadingGraph).ConfigureAwait(false);
                var input = new NavigationFlowInput(costs, profile, value.Goals, value.GoalVersion, value.Version, headingGraph);
                NavigationFlow flow;
                if (value.Source is { } source)
                {
                    using NavigationFlowAsset asset = await NavigationFlowAsset.OpenAsync(loader, source.Asset, cancellationToken).ConfigureAwait(false);
                    if (source.Heading)
                    {
                        flow = (await asset.GetBlockAsync<HeadingNavigationFlowBlock>(cancellationToken).ConfigureAwait(false)).Create(input, scheduler);
                        restored.Sources[flow] = source;
                        return flow;
                    }
                    await asset.GetBlockAsync<NavigationFlowBlock>(cancellationToken).ConfigureAwait(false);
                }
                flow = new(input);
                if (value.Solver == FastIterativeFlow.Id)
                    new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler).Schedule().Complete();
                else new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
                if (value.Source is { } savedSource) restored.Sources[flow] = savedSource;
                return flow;
            default: throw new InvalidDataException($"Unknown navigation save resource {record.GetType().Name}.");
        }
        if (record is SavedProfile or SavedPolicy or SavedMesh or SavedHeadingMesh or SavedGrid or SavedMotions or SavedGeometry or SavedLinks)
            restored.Sources[result is NavigationGrid grid ? NavigationSaveResources.GetGridSource(grid) : result] = record;
        return result;
    }

    private NavigationCosts RestoreCosts(SavedCosts saved, NavigationGrid grid, NavigationPolicy policy)
    {
        NavigationCosts baseline = NavigationCostSystem.Schedule(grid, policy, saved.Version, scheduler).Complete().Costs;
        float[] cells = baseline.CellCosts.ToArray(), edges = baseline.EdgeCosts.ToArray();
        var indices = new Dictionary<SavedCell, int>();
        for (int index = 0; index < grid.Cells.Length; index++)
        {
            NavigationGridCell cell = grid.Cells.Span[index];
            indices.Add(new(cell.Position, cell.Layer), index);
        }
        foreach (SavedCellCost edit in saved.Cells)
            if (indices.TryGetValue(edit.Cell, out int cell)) cells[cell] = edit.Cost;
        foreach (SavedEdgeCost edit in saved.Edges)
        {
            if (!indices.TryGetValue(edit.From, out int from) || !indices.TryGetValue(edit.To, out int to)) continue;
            NavigationGridCell cell = grid.Cells.Span[from];
            for (int edge = cell.EdgeStart; edge < cell.EdgeStart + cell.EdgeCount; edge++)
            {
                NavigationGridEdge transition = grid.Edges.Span[edge];
                if (transition.ToCell == to && transition.Action == edit.Action && transition.RequiredCapabilities == edit.Capabilities)
                    edges[edge] = edit.Cost;
            }
        }
        return new(grid, policy, cells, edges, saved.Version);
    }
}
