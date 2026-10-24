using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Resolved pose connections and their execution level in one installed graph.</summary>
public sealed class PoseNode
{
    private readonly PoseResultReference[] _inputs;
    public PoseResultReference Output { get; }
    public ReadOnlySpan<PoseResultReference> Inputs => _inputs;
    public int Level { get; internal set; } = -1;

    internal PoseNode(PoseResultReference output, ReadOnlySpan<PoseResultReference> inputs) =>
        (Output, _inputs) = (output, inputs.ToArray());
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
    internal int Depth;
    internal bool Complete;
}

public static partial class PoseAnimation
{
    /// <summary>Registers a concrete leaf's resolved connections during installation.</summary>
    public static PoseNode RegisterNode(World world, Entity animation, PoseResultReference output,
        ReadOnlySpan<PoseResultReference> inputs, in AnimationLeafPlacement placement)
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
        var node = new PoseNode(output, inputs);
        world.ExecuteBufferWrite<PoseNodeEntry, PoseNode>(animation, ref node,
            static (nodes, ref node) => nodes.Add(new PoseNodeEntry { Node = node }));
        return node;
    }

    /// <summary>Compiles the installed graph's acyclic pose connections after all leaves are installed.</summary>
    public static void CompleteNodes(World world, Entity animation)
    {
        PoseNodeGraph graph = world.Read<PoseNodeGraph>(animation);
        if (graph.Complete) return;
        int depth = -1;
        world.ExecuteBufferWrite<PoseNodeEntry, int>(animation, ref depth, static (nodes, ref depth) =>
        {
            Span<PoseNodeEntry> entries = nodes.AsSpan();
            foreach (PoseNodeEntry entry in entries) entry.Node.Level = -1;
            for (int index = 0; index < entries.Length; index++) depth = Math.Max(depth, AssignLevel(entries, index));
        });
        graph.Depth = depth;
        graph.Complete = true;
        world.Replace(animation, graph);
    }

    private static int AssignLevel(Span<PoseNodeEntry> nodes, int index)
    {
        PoseNode node = nodes[index].Node;
        if (node.Level >= 0) return node.Level;
        int level = 0;
        foreach (PoseResultReference input in node.Inputs)
            for (int producer = 0; producer < nodes.Length; producer++)
                if (producer != index && nodes[producer].Node.Output == input)
                    level = Math.Max(level, AssignLevel(nodes, producer) + 1);
        return node.Level = level;
    }

    /// <summary>Releases the installed connection table with the graph's concrete leaf state.</summary>
    public static void ReleaseNodes(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PoseNodeEntry>(animation, static nodes => nodes.Clear());
        PoseNodeGraph graph = world.Read<PoseNodeGraph>(animation);
        graph.Depth = -1;
        graph.Complete = false;
        world.Replace(animation, graph);
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

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _nodes = new(new PoseNodeDriver(context.World));
        configure(_nodes);
        _graphs = context.World.Query(context.World.QueryDefinition().Read<PoseNodeGraph>().Read<Timeline>().Enabled<Timeline>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        int depth = -1;
        context.World.ExecuteQuery(_graphs, ref depth, static (QueryCursor rows, ref int depth) =>
        {
            foreach (QueryRow row in rows.Rows) depth = Math.Max(depth, row.Read<PoseNodeGraph>().Depth);
        });
        for (int level = 0; level <= depth; level++)
        {
            var nodes = new PoseNodeContext(context, level);
            _nodes.Update(ref nodes);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _nodes.Dispose();
        context.World.ReleaseQuery(_graphs);
    }
}
