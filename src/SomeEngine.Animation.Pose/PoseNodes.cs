using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Resolved pose connections, model dependencies and execution level.</summary>
public sealed class PoseNode
{
    private readonly PoseResultReference[] _inputs;
    private readonly short[] _modelBones;
    public PoseResultReference Output { get; }
    public ReadOnlySpan<PoseResultReference> Inputs => _inputs;
    /// <summary>Connected bones required by this model-space algorithm.</summary>
    public ReadOnlySpan<short> ModelBones => _modelBones;
    internal PoseResultReference[] ModelInputs = [];
    public int Level { get; internal set; } = -1;

    internal PoseNode(PoseResultReference output, ReadOnlySpan<PoseResultReference> inputs, ReadOnlySpan<short> modelBones) =>
        (Output, _inputs, _modelBones) = (output, inputs.ToArray(), modelBones.ToArray());
}

/// <summary>The frame and compiled dependency level being evaluated by concrete pose systems.</summary>
public readonly record struct PoseNodeContext(ImmediateSystemContext Frame, int Level);

[BufferCapacity(1)]
internal struct PoseNodeEntry : IBufferElement
{
    internal PoseNode Node;
}

internal struct PoseNodeGraph : IComponent
{
    internal uint Starts;
    internal bool Complete;
}

public static partial class PoseAnimation
{
    /// <summary>Registers a concrete leaf's resolved connections during installation.</summary>
    public static PoseNode RegisterNode(World world, Entity animation, PoseResultReference output,
        ReadOnlySpan<PoseResultReference> inputs, in AnimationLeafPlacement placement, ReadOnlySpan<short> modelBones = default)
    {
        if (!world.HasBuffer<PoseNodeEntry>(animation)) world.AddBuffer<PoseNodeEntry>(animation);
        if (!world.Has<PoseNodeGraph>(animation)) world.Add(animation, new PoseNodeGraph { Starts = placement.Starts });
        PoseNodeGraph graph = world.Read<PoseNodeGraph>(animation);
        if (graph.Starts != placement.Starts)
        {
            world.ExecuteBufferWrite<PoseNodeEntry>(animation, static nodes => nodes.Clear());
            graph = new PoseNodeGraph { Starts = placement.Starts };
        }
        graph.Complete = false;
        world.Replace(animation, graph);
        var node = new PoseNode(output, inputs, modelBones);
        world.ExecuteBufferWrite<PoseNodeEntry, PoseNode>(animation, ref node,
            static (nodes, ref node) => nodes.Add(new PoseNodeEntry { Node = node }));
        CpuBoneDemands.For(world).Changed = true;
        return node;
    }

    /// <summary>Compiles the installed graph's acyclic pose connections after all leaves are installed.</summary>
    public static void CompleteNodes(World world, Entity animation)
    {
        PoseNodeGraph graph = world.Read<PoseNodeGraph>(animation);
        if (graph.Complete) return;
        graph.Complete = true;
        world.Replace(animation, graph);
        RefreshModelDependencies(world);
    }

    internal static Dictionary<PoseResultReference, PoseNode> ReadNodes(World world)
    {
        var nodes = new Dictionary<PoseResultReference, PoseNode>();
        QueryHandle query = world.Query(new QueryDefinitionBuilder().ReadBuffer<PoseNodeEntry>());
        world.ExecuteQuery(query, ref nodes, static (scoped QueryCursor rows, ref Dictionary<PoseResultReference, PoseNode> nodes) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (PoseNodeEntry entry in row.ReadBuffer<PoseNodeEntry>().AsSpan()) nodes[entry.Node.Output] = entry.Node;
        });
        world.ReleaseQuery(query);
        return nodes;
    }

    internal static void RefreshModelDependencies(World world)
    {
        Dictionary<PoseResultReference, PoseNode> nodes = ReadNodes(world);
        Dictionary<SkeletonInstance, PoseResultReference> sources = ReadModelSources(world);
        foreach (PoseNode node in nodes.Values)
        {
            node.Level = -1;
            var parents = new List<PoseResultReference>();
            if (node.ModelBones.Length != 0)
                for (SkeletonInstance? parent = world.Read<PoseTarget>(node.Output.Entity).Skeleton.ParentInstance;
                    parent is not null; parent = parent.ParentInstance)
                    if (sources.TryGetValue(parent, out PoseResultReference source)) parents.Add(source);
            node.ModelInputs = parents.ToArray();
        }
        foreach (PoseNode node in nodes.Values) AssignLevel(node);

        int AssignLevel(PoseNode node)
        {
            if (node.Level >= 0) return node.Level;
            int level = 0;
            foreach (PoseResultReference input in node.Inputs)
                if (nodes.TryGetValue(input, out PoseNode? producer)) level = Math.Max(level, AssignLevel(producer) + 1);
            foreach (PoseResultReference input in node.ModelInputs)
                if (nodes.TryGetValue(input, out PoseNode? producer)) level = Math.Max(level, AssignLevel(producer) + 1);
            return node.Level = level;
        }
    }

    internal static Dictionary<SkeletonInstance, PoseResultReference> ReadModelSources(World world)
    {
        var sources = new Dictionary<SkeletonInstance, PoseResultReference>();
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<PoseTarget>());
        world.ExecuteQuery(targets, ref sources, static (scoped QueryCursor rows, ref Dictionary<SkeletonInstance, PoseResultReference> sources) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                PoseTarget target = row.Read<PoseTarget>();
                sources[target.Skeleton] = target.Output;
            }
        });
        world.ReleaseQuery(targets);
        targets = world.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());
        world.ExecuteQuery(targets, ref sources, static (scoped QueryCursor rows, ref Dictionary<SkeletonInstance, PoseResultReference> sources) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GpuPoseTarget target = row.Read<GpuPoseTarget>();
                if (target.CpuBones?.Source is { } source) sources[target.Skeleton] = source;
            }
        });
        world.ReleaseQuery(targets);
        return sources;
    }

    /// <summary>Releases the installed connection table with the graph's concrete leaf state.</summary>
    public static void ReleaseNodes(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PoseNodeEntry>(animation, static nodes => nodes.Clear());
        PoseNodeGraph graph = world.Read<PoseNodeGraph>(animation);
        graph.Complete = false;
        world.Replace(animation, graph);
        CpuBoneDemands.For(world).Changed = true;
        RefreshModelDependencies(world);
    }
}

internal sealed class PoseNodeDriver(World world) : ISystemDriver<PoseNodeContext>
{
    public uint AcquireSystemVersion(ref SystemSlot slot) => world.AcquireSystemVersion();

    public PoseNodeContext CreateContext(ref SystemSlot slot) =>
        new(new(world, slot.LastSystemVersion, slot.CurrentSystemVersion), 0);

    public void CreateContext(ref SystemSlot slot, ref PoseNodeContext context) =>
        context = new(new(world, slot.LastSystemVersion, slot.CurrentSystemVersion, context.Frame.DeltaTime), context.Level);
}

internal struct PoseNodeSystem(Action<SystemGroup<PoseNodeContext>> configure) : ISystem<ImmediateSystemContext>
{
    private SystemGroup<PoseNodeContext> _nodes = null!;
    private QueryHandle _graphs;
    private PoseCompositionSystem _models;
    private double _time;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _nodes = new(new PoseNodeDriver(context.World));
        configure(_nodes);
        _models.OnCreate(ref context);
        _graphs = context.World.Query(context.World.QueryDefinition().Read<PoseNodeGraph>().ReadBuffer<PoseNodeEntry>().Read<Timeline>().Enabled<Timeline>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        CpuBoneDemands cpu = CpuBoneDemands.For(context.World);
        cpu.EnsureBound();
        int depth = -1;
        context.World.ExecuteQuery(_graphs, ref depth, static (scoped QueryCursor rows, ref int depth) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (PoseNodeEntry entry in row.ReadBuffer<PoseNodeEntry>().AsSpan()) depth = Math.Max(depth, entry.Node.Level);
        });
        for (int level = 0; level <= depth; level++)
        {
            _models.CopyInputs(context.World);
            cpu.Complete(_time, level - 1);
            _models.ComposeModels(context.World, _time);
            var nodes = new PoseNodeContext(context, level);
            _nodes.Update(ref nodes);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _nodes.Dispose();
        _models.OnDestroy(ref context);
        context.World.ReleaseQuery(_graphs);
    }
}
