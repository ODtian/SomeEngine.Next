using SomeEngine.Render.Components;
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
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace =
        new("render", "Prepare.ClusterResidency");
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
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
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
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace =
        new("render", "Prepare.ClusterInstances");
    private readonly ClusterRenderResources _resources;
    private readonly QueryDefinition _entities;
    private readonly ClusterBatchComposer _composer;
    private readonly InstanceColumnChanges _columnChanges = new();
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
        _entities = (entities ?? throw new ArgumentNullException(nameof(entities))).WithOptionalRead<RenderDeformBounds>();
        ArgumentNullException.ThrowIfNull(exactShaderLayout);
        var combined = new RenderInstanceProducerBundle<
            ClusterRenderResources.ClusterGeometryProducer,
            TProducer>(
                exactShaderLayout,
                resources.CreateGeometryProducer(),
                additional);
        _composer = new ClusterBatchComposer(exactShaderLayout, combined, _columnChanges);
        _builder = new RenderInstanceBatchBuilder<RenderInstanceSingleGroup>(
            groupingPartitions: 1,
            rowsPerPacket);
    }

    public QueryDefinition EntityQuery => _entities;

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
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
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
            _columnChanges.Reset(context.LastSystemVersion);
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

    private sealed class InstanceColumnChanges
    {
        internal const int CurrentTransform = 1, PreviousTransform = 2, Mesh = 4, Additional = 8;
        internal const int InitialValues = 16, Xray = 32;
        internal const int All = CurrentTransform | PreviousTransform | Mesh | Additional | InitialValues | Xray;
        private int _fields;
        private int _partialTransformChanges;
        internal uint LastVersion { get; private set; }
        internal int Fields => Volatile.Read(ref _fields);
        internal bool AllTransformsChanged => Volatile.Read(ref _partialTransformChanges) == 0;
        internal void Reset(uint lastVersion)
        {
            LastVersion = lastVersion;
            _fields = 0;
            _partialTransformChanges = 0;
        }
        internal void Add(int fields) => Interlocked.Or(ref _fields, fields);
        internal void RecordPartialTransformChanges() => Interlocked.Exchange(ref _partialTransformChanges, 1);
    }

    private readonly struct ClusterBatchComposer(
        RenderInstancePropertyLayout layout,
        RenderInstanceProducerBundle<
            ClusterRenderResources.ClusterGeometryProducer,
            TProducer> producer,
        InstanceColumnChanges columns) :
        IRenderInstanceBatchComposer<RenderInstanceSingleGroup>
    {
        public RenderInstanceChanges GetChanges(
            ReadOnlyQueryPacket packet,
            uint lastSystemVersion) =>
            producer.GetChanges(packet, lastSystemVersion);

        public RenderInstanceChanges CollectChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion)
        {
            if (!packet.AllRowsChangedSince<RenderTransform>(lastSystemVersion) ||
                !packet.AllRowsChangedSince<RenderPreviousTransform>(lastSystemVersion))
                columns.RecordPartialTransformChanges();
            RenderInstanceChanges changes = GetChanges(packet, lastSystemVersion);
            if ((changes & RenderInstanceChanges.Structure) != 0)
                columns.Add(InstanceColumnChanges.All);
            else if (changes != RenderInstanceChanges.None)
            {
                int fields = 0;
                if (packet.ChangedSince<RenderTransform>(lastSystemVersion)) fields |= InstanceColumnChanges.CurrentTransform;
                if (packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)) fields |= InstanceColumnChanges.PreviousTransform;
                if (packet.ChangedSince<RenderClusterGeometry>(lastSystemVersion) ||
                    packet.Has<RenderDeformBounds>() && packet.ChangedSince<RenderDeformBounds>(lastSystemVersion)) fields |= InstanceColumnChanges.Mesh;
                if (producer.Second.GetChanges(packet, lastSystemVersion) != RenderInstanceChanges.None)
                    fields |= InstanceColumnChanges.Additional;
                columns.Add(fields);
            }
            return changes;
        }

        public bool AllRowsChanged(
            ReadOnlyQueryPacket packet,
            uint lastSystemVersion) =>
            columns.AllTransformsChanged || packet.AllRowsChangedSince<RenderTransform>(lastSystemVersion)
            || packet.AllRowsChangedSince<RenderPreviousTransform>(lastSystemVersion)
            || packet.AllRowsChangedSince<RenderClusterGeometry>(lastSystemVersion);

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

        public RenderInstancePropertyLayout? GetReplacedProperties(in RenderInstanceSingleGroup group, bool writeAll) =>
            writeAll || columns.AllTransformsChanged ? RenderInstanceTransformProperties.Layout : null;

        public void Bind(
            in RenderInstanceSingleGroup group,
            RenderInstanceWriteSlice destination)
        {
            columns.Add(InstanceColumnChanges.All);
            producer.Bind(destination);
        }

        public void BindUpdate(in RenderInstanceSingleGroup group,
            RenderInstanceWriteSlice destination, bool writeAll)
        {
            if (writeAll) columns.Add(InstanceColumnChanges.All);
            int fields = columns.Fields;
            producer.First.Bind(destination.Restrict(producer.First.Properties),
                (fields & InstanceColumnChanges.CurrentTransform) != 0,
                (fields & InstanceColumnChanges.PreviousTransform) != 0,
                (fields & InstanceColumnChanges.Mesh) != 0,
                (fields & InstanceColumnChanges.Xray) != 0);
            if ((fields & InstanceColumnChanges.Additional) != 0)
                producer.Second.Bind(destination.Restrict(producer.Second.Properties));
        }

        public void Write(
            in RenderInstanceSingleGroup group,
            int groupIndex,
            ReadOnlyQueryPacket packet,
            int entityRow,
            RenderInstanceWriteSlice destination) =>
            WritePacket(in group, groupIndex, packet.Slice(entityRow, 1), destination);

        public void WritePacket(
            in RenderInstanceSingleGroup group,
            int groupIndex,
            ReadOnlyQueryPacket packet,
            RenderInstanceWriteSlice destination)
        {
            if (groupIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(groupIndex));
            int fields = columns.Fields;
            bool all = (fields & InstanceColumnChanges.InitialValues) != 0;
            uint lastVersion = columns.LastVersion;
            producer.First.Write(destination.Restrict(producer.First.Properties), packet,
                all || (fields & InstanceColumnChanges.CurrentTransform) != 0 && packet.ChangedSince<RenderTransform>(lastVersion),
                all || (fields & InstanceColumnChanges.PreviousTransform) != 0 && packet.ChangedSince<RenderPreviousTransform>(lastVersion),
                all || (fields & InstanceColumnChanges.Mesh) != 0 && (packet.ChangedSince<RenderClusterGeometry>(lastVersion) ||
                    packet.Has<RenderDeformBounds>() && packet.ChangedSince<RenderDeformBounds>(lastVersion)),
                all || (fields & InstanceColumnChanges.Xray) != 0 && packet.Has<RenderXray>() && packet.ChangedSince<RenderXray>(lastVersion));
            if (all || (fields & InstanceColumnChanges.Additional) != 0 &&
                producer.Second.GetChanges(packet, lastVersion) != RenderInstanceChanges.None)
                producer.Second.Write(destination.Restrict(producer.Second.Properties), packet);
        }
    }
}
