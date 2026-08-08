using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Publishes Cluster-owned residency changes at the render prepare boundary. It owns no instance
/// set and no batch; instance projection is a separate system concern.
/// </summary>
public sealed class ClusterResidencySystem : ISystem<RenderPrepareSystemContext>
{
    private readonly ClusterRenderResources _resources;

    public ClusterResidencySystem(ClusterRenderResources resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public ClusterPrepareResult LastResult { get; private set; }

    public void OnCreate(ref RenderPrepareSystemContext context) =>
        RequireWorld(context.World);

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        RequireWorld(context.World);
        LastResult = _resources.Prepare(context.ActiveScope);
    }

    private void RequireWorld(RenderWorld world)
    {
        if (!ReferenceEquals(world, _resources.World))
        {
            throw new InvalidOperationException(
                "The Cluster residency system is installed in a different RenderWorld.");
        }
    }
}

/// <summary>
/// Installs Cluster's one unclassified all-GPU instance composition. The engine-wide instance
/// storage owns allocation and transport; this system owns only a query, the generic physical
/// composition root, and Cluster's geometry-field contribution. It keeps no second world or
/// entity list and performs no CPU material/pass binning.
/// </summary>
public sealed class ClusterInstanceSystem<TProducer> :
    ISystem<RenderPrepareSystemContext>,
    IRenderInstanceBatchSource<RenderInstanceSingleGroup>
    where TProducer : struct, IRenderInstanceProducer
{
    private readonly ClusterRenderResources _resources;
    private readonly QueryDefinition _entities;
    private readonly ClusterBatchComposer _composer;
    private readonly RenderInstanceBatchBuilder<RenderInstanceSingleGroup> _builder;
    private QueryHandle _entityQuery;
    private int _publishedMeshCount;
    private bool _created;

    public ClusterInstanceSystem(
        ClusterRenderResources resources,
        QueryDefinition entities,
        RenderInstancePropertyLayout exactShaderLayout,
        TProducer additional,
        int rowsPerPacket = 0)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        ArgumentNullException.ThrowIfNull(exactShaderLayout);
        var combined = new RenderInstanceProducerBundle<
            ClusterRenderResources.ClusterGeometryProducer,
            TProducer>(
                exactShaderLayout,
                resources.CreateGeometryProducer(),
                additional);
        _composer = new ClusterBatchComposer(exactShaderLayout, combined);
        _builder = new RenderInstanceBatchBuilder<RenderInstanceSingleGroup>(
            groupingPartitions: 1,
            rowsPerPacket);
    }

    public RenderInstanceBatches<RenderInstanceSingleGroup>? Current => _builder.Current;

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        RequireWorld(context.World);
        if (_created)
            throw new InvalidOperationException("The Cluster instance system is already created.");

        _entityQuery = context.World.Query(_entities);
        _publishedMeshCount = _resources.PublishedMeshCount;
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        RequireWorld(context.World);
        if (!_created)
            throw new InvalidOperationException("The Cluster instance system was not created.");

        int publishedMeshCount = _resources.PublishedMeshCount;
        RenderInstanceChanges forcedChanges = publishedMeshCount == _publishedMeshCount
            ? RenderInstanceChanges.None
            : RenderInstanceChanges.Values;
        _resources.EnterInstanceComposition();
        try
        {
            _ = _builder.Update(
                context,
                _entityQuery,
                in _composer,
                forcedChanges);
            _publishedMeshCount = publishedMeshCount;
        }
        finally
        {
            _resources.ExitInstanceComposition();
        }
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;

        List<Exception>? failures = null;
        try
        {
            _builder.Clear(context);
        }
        catch (Exception failure)
        {
            failures = [failure];
        }

        try
        {
            context.World.ReleaseQuery(_entityQuery);
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
        _entityQuery = default;
        _publishedMeshCount = 0;
        _created = false;
        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "Cluster instance composition could not release every owned resource.",
                    failures);
        }
    }

    private void RequireWorld(RenderWorld world)
    {
        if (!ReferenceEquals(world, _resources.World))
        {
            throw new InvalidOperationException(
                "The Cluster instance system is installed in a different RenderWorld.");
        }
    }

    private readonly struct ClusterBatchComposer(
        RenderInstancePropertyLayout layout,
        RenderInstanceProducerBundle<
            ClusterRenderResources.ClusterGeometryProducer,
            TProducer> producer) :
        IRenderInstanceBatchComposer<RenderInstanceSingleGroup>
    {
        public RenderInstanceChanges GetChanges(
            ReadOnlyQueryPacket packet,
            uint lastSystemVersion) =>
            producer.GetChanges(packet, lastSystemVersion);

        public int CountGroups(ReadOnlyQueryPacket packet, int entityRow) => 1;

        public RenderInstanceSingleGroup GetGroup(
            ReadOnlyQueryPacket packet,
            int entityRow,
            int groupIndex) =>
            groupIndex == 0
                ? default
                : throw new ArgumentOutOfRangeException(nameof(groupIndex));

        public RenderInstancePropertyLayout GetLayout(
            in RenderInstanceSingleGroup group) => layout;

        public void Bind(
            in RenderInstanceSingleGroup group,
            RenderInstanceWriteSlice destination) =>
            producer.Bind(destination);

        public void Write(
            in RenderInstanceSingleGroup group,
            int groupIndex,
            ReadOnlyQueryPacket packet,
            int entityRow,
            RenderInstanceWriteSlice destination) =>
            producer.Write(destination, packet.Slice(entityRow, 1));

        public void WritePacket(
            in RenderInstanceSingleGroup group,
            int groupIndex,
            ReadOnlyQueryPacket packet,
            RenderInstanceWriteSlice destination)
        {
            if (groupIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(groupIndex));
            producer.Write(destination, packet);
        }
    }
}
