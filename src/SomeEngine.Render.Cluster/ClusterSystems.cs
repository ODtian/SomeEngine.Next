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
/// storage owns allocation and transport; the generic composition system owns the query and
/// batch lifecycle. This adapter supplies Cluster's residency invalidation and geometry-field
/// contribution. It keeps no second world or entity list and performs no CPU material/pass binning.
/// </summary>
public sealed class ClusterInstanceSystem<TProducer> :
    ISystem<RenderPrepareSystemContext>,
    IRenderInstanceBatchSource<RenderInstanceSingleGroup>
    where TProducer : struct, IRenderInstanceProducer
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace =
        new("render", "Prepare.ClusterInstances");
    private readonly ClusterRenderResources _resources;
    private readonly InstanceColumnChanges _columnChanges = new();
    private readonly RenderInstanceCompositionSystem<RenderInstanceSingleGroup, ClusterBatchComposer> _composition;
    private int _publishedMeshCount;

    public ClusterInstanceSystem(
        ClusterRenderResources resources,
        QueryDefinition entities,
        RenderInstancePropertyLayout exactShaderLayout,
        TProducer additional,
        int rowsPerPacket = 0)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        entities = (entities ?? throw new ArgumentNullException(nameof(entities)))
            .WithOptionalRead<RenderDeformBounds>().WithOptionalRead<RenderMesh>().WithOptionalRead<RenderXray>();
        ArgumentNullException.ThrowIfNull(exactShaderLayout);
        var geometryAndAdditional = new RenderInstanceProducerBundle<
            ClusterRenderResources.ClusterGeometryProducer,
            TProducer>(resources.CreateGeometryProducer(), additional);
        var combined = new RenderInstanceProducerBundle<
            RenderInstanceTransformProducer,
            RenderInstanceProducerBundle<ClusterRenderResources.ClusterGeometryProducer, TProducer>>(
                exactShaderLayout,
                new RenderInstanceTransformProducer(),
                geometryAndAdditional);
        var composer = new ClusterBatchComposer(exactShaderLayout, combined, _columnChanges);
        _composition = new RenderInstanceCompositionSystem<RenderInstanceSingleGroup, ClusterBatchComposer>(
            entities,
            in composer,
            groupingPartitions: 1,
            rowsPerPacket);
    }

    public QueryDefinition EntityQuery => _composition.EntityQuery;

    public QueryHandle EntityQueryHandle => _composition.EntityQueryHandle;

    public RenderInstanceBatches<RenderInstanceSingleGroup>? Current => _composition.Current;

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        RequireWorld(context.World);
        if (_composition.IsCreated)
            throw new InvalidOperationException("The Cluster instance system is already created.");

        int publishedMeshCount = _resources.PublishedMeshCount;
        _composition.OnCreate(ref context);
        _publishedMeshCount = publishedMeshCount;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        RequireWorld(context.World);
        if (!_composition.IsCreated)
            throw new InvalidOperationException("The Cluster instance system was not created.");

        int publishedMeshCount = _resources.PublishedMeshCount;
        RenderInstanceChanges forcedChanges = publishedMeshCount == _publishedMeshCount
            ? RenderInstanceChanges.None
            : RenderInstanceChanges.Values;
        _resources.EnterInstanceComposition();
        try
        {
            _columnChanges.Reset(context.LastSystemVersion);
            _composition.OnUpdate(ref context, forcedChanges);
            _publishedMeshCount = publishedMeshCount;
        }
        finally
        {
            _resources.ExitInstanceComposition();
        }
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_composition.IsCreated)
            return;

        try
        {
            _composition.OnDestroy(ref context);
        }
        finally
        {
            _publishedMeshCount = 0;
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
            RenderInstanceTransformProducer,
            RenderInstanceProducerBundle<ClusterRenderResources.ClusterGeometryProducer, TProducer>> producer,
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
            RenderInstanceChanges changes = producer.First.GetChanges(packet, lastSystemVersion)
                | producer.Second.First.GetChanges(packet, lastSystemVersion);
            RenderInstanceChanges additionalChanges = producer.Second.Second.GetChanges(packet, lastSystemVersion);
            changes |= additionalChanges;
            if ((changes & RenderInstanceChanges.Structure) != 0)
                columns.Add(InstanceColumnChanges.All);
            else if (changes != RenderInstanceChanges.None)
            {
                int fields = 0;
                if (packet.ChangedSince<RenderTransform>(lastSystemVersion)) fields |= InstanceColumnChanges.CurrentTransform;
                if (packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)) fields |= InstanceColumnChanges.PreviousTransform;
                if (ClusterRenderResources.ClusterGeometryProducer.MeshChanged(packet, lastSystemVersion))
                    fields |= InstanceColumnChanges.Mesh;
                if (ClusterRenderResources.ClusterGeometryProducer.XrayChanged(packet, lastSystemVersion))
                    fields |= InstanceColumnChanges.Xray;
                if (additionalChanges != RenderInstanceChanges.None)
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
                (fields & InstanceColumnChanges.PreviousTransform) != 0);
            producer.Second.First.Bind(destination.Restrict(producer.Second.First.Properties),
                (fields & InstanceColumnChanges.Mesh) != 0,
                (fields & InstanceColumnChanges.Xray) != 0);
            if ((fields & InstanceColumnChanges.Additional) != 0)
                producer.Second.Second.Bind(destination.Restrict(producer.Second.Second.Properties));
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
            if (all)
            {
                producer.Write(destination, packet);
                return;
            }
            producer.First.WriteChanged(destination.Restrict(producer.First.Properties), packet,
                lastVersion, columns.AllTransformsChanged);
            producer.Second.First.WriteChanged(destination.Restrict(producer.Second.First.Properties), packet, lastVersion);
            if ((fields & InstanceColumnChanges.Additional) != 0)
                producer.Second.Second.WriteChanged(destination.Restrict(producer.Second.Second.Properties), packet, lastVersion);
        }
    }
}
