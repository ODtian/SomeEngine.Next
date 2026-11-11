using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
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
        if (!world.Has<Buffer<PoseNodeEntry>>(animation)) world.AddBuffer<PoseNodeEntry>(animation);
        if (!world.Has<PoseNodeGraph>(animation)) world.Add(animation, new PoseNodeGraph { Starts = placement.Starts });
        PoseNodeGraph graph = world.ReadComponentData<PoseNodeGraph>(animation);
        if (graph.Starts != placement.Starts)
        {
            {
                using var nodes = world.GetBuffer<PoseNodeEntry>(animation);
                nodes.Clear();

            }
            graph = new PoseNodeGraph { Starts = placement.Starts };
        }
        graph.Complete = false;
        world.Set(animation, graph);
        var node = new PoseNode(output, inputs, modelBones);
        {
            using var nodes = world.GetBuffer<PoseNodeEntry>(animation);
            Visit(nodes, ref node);

            static void Visit(DynamicBuffer<PoseNodeEntry> nodes, ref PoseNode node)
            {
                nodes.Add(new PoseNodeEntry { Node = node });

            }
        }
        CpuBoneDemands.For(world).Changed = true;
        return node;
    }

    /// <summary>Compiles the installed graph's acyclic pose connections after all leaves are installed.</summary>
    public static void CompleteNodes(World world, Entity animation)
    {
        PoseNodeGraph graph = world.ReadComponentData<PoseNodeGraph>(animation);
        if (graph.Complete) return;
        graph.Complete = true;
        world.Set(animation, graph);
        RefreshModelDependencies(world);
    }

    internal static Dictionary<PoseResultReference, PoseNode> ReadNodes(World world)
    {
        var nodes = new Dictionary<PoseResultReference, PoseNode>();
        Query query = world.Query(new QueryDescription().WithStored<Buffer<PoseNodeEntry>>());
        {
            using QueryScope read = query.Open();
            Visit(in read, ref nodes);

            static void Visit(scoped in QueryScope rows, ref Dictionary<PoseResultReference, PoseNode> nodes)
            {
                foreach (QueryChunk chunk in rows)
                {

                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (PoseNodeEntry entry in chunk.ReadBuffer<PoseNodeEntry>(rowIndex).AsSpan()) nodes[entry.Node.Output] = entry.Node;
                    }
                }

            }
        }

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
                for (SkeletonInstance? parent = world.ReadComponentData<PoseTarget>(node.Output.Entity).Skeleton.ParentInstance;
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
        Query targets = world.Query(new QueryDescription().WithStored<PoseTarget>());
        {
            using QueryScope read = targets.Open();
            Visit(in read, ref sources);

            static void Visit(scoped in QueryScope rows, ref Dictionary<SkeletonInstance, PoseResultReference> sources)
            {
                foreach (QueryChunk chunk in rows)
                {
                    chunk.TryRead<PoseTarget>(out var values0);
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {

                        PoseTarget target = values0[rowIndex];
                        sources[target.Skeleton] = target.Output;

                    }
                }

            }
        }

        targets = world.Query(new QueryDescription().WithStored<GpuPoseTarget>());
        {
            using QueryScope read = targets.Open();
            Visit(in read, ref sources);

            static void Visit(scoped in QueryScope rows, ref Dictionary<SkeletonInstance, PoseResultReference> sources)
            {
                foreach (QueryChunk chunk in rows)
                {
                    chunk.TryRead<GpuPoseTarget>(out var values0);
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {

                        GpuPoseTarget target = values0[rowIndex];
                        if (target.CpuBones?.Source is { } source) sources[target.Skeleton] = source;

                    }
                }

            }
        }

        return sources;
    }

    /// <summary>Releases the installed connection table with the graph's concrete leaf state.</summary>
    public static void ReleaseNodes(World world, Entity animation)
    {
        {
            using var nodes = world.GetBuffer<PoseNodeEntry>(animation);
            nodes.Clear();

        }
        PoseNodeGraph graph = world.ReadComponentData<PoseNodeGraph>(animation);
        graph.Complete = false;
        world.Set(animation, graph);
        CpuBoneDemands.For(world).Changed = true;
        RefreshModelDependencies(world);
    }
}

internal sealed class PoseNodeDriver(World world) : ISystemDriver<PoseNodeContext>
{
    public World World => world;
    public ulong AcquireSystemVersion(ref SystemState state) => world.AcquireSystemVersion();

    public PoseNodeContext CreateContext(ref SystemState state) =>
        new(new(world, state.LastSystemVersion, state.CurrentSystemVersion), 0);

    public void CreateContext(ref SystemState state, ref PoseNodeContext context) =>
        context = new(new(world, state.LastSystemVersion, state.CurrentSystemVersion, context.Frame.DeltaTime), context.Level);
}

internal struct PoseNodeSystem(Action<SystemGroup<PoseNodeContext>> configure) : ISystem<ImmediateSystemContext>
{
    private SystemGroup<PoseNodeContext> _nodes = null!;
    private Query _graphs;
    private PoseCompositionSystem _models;
    private double _time;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _nodes = new(new PoseNodeDriver(context.World));
        configure(_nodes);
        _models.OnCreate(ref context);
        _graphs = context.World.Query(new QueryDescription().WithStored<PoseNodeGraph>().WithStored<Buffer<PoseNodeEntry>>().WithStored<Timeline>().With<Timeline>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        CpuBoneDemands cpu = CpuBoneDemands.For(context.World);
        cpu.EnsureBound();
        int depth = -1;
        {
            using QueryScope read = _graphs.Open();
            Visit(in read, ref depth);

            static void Visit(scoped in QueryScope rows, ref int depth)
            {
                foreach (QueryChunk chunk in rows)
                {

                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (PoseNodeEntry entry in chunk.ReadBuffer<PoseNodeEntry>(rowIndex).AsSpan()) depth = Math.Max(depth, entry.Node.Level);
                    }
                }

            }
        }
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

    }
}
