using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>Builds property animations with pooled data and nested scopes.</summary>
public unsafe ref partial struct AnimationBuilder
{
    public const int TrackCapacity = 64;
    private BuildData[]? _data;

    public AnimationBuilder(World world)
    {
        _data = ArrayPool<BuildData>.Shared.Rent(1);
        _data[0] = new BuildData { World = world };
        GraphBuilding.Initialize(_data);
    }

    public void To<T>(Entity target, Channel<T> channel, T value, Spec spec, byte layer = 0) where T : unmanaged =>
        AddTrack(target, channel, value, spec, layer, TrackEndpoints.Values);

    public void By<T>(Entity target, Channel<T> channel, T delta, Spec spec, byte layer = 0) where T : unmanaged =>
        AddTrack(target, channel, delta, spec, layer, TrackEndpoints.Displacement);

    private void AddTrack<T>(Entity target, Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged
    {
        if (GraphCurves<T>.TryAdd(ref this, _data![0].World, target, channel, value, spec, layer, endpoints))
            return;
        ref BuildData data = ref _data![0];
        int offset = data.ValueCount;
        int size = Unsafe.SizeOf<Channel<T>>() + Unsafe.SizeOf<T>();
        GraphArrays.Grow(ref data.Values, offset + size);
        Unsafe.WriteUnaligned(ref data.Values![offset], channel);
        Unsafe.WriteUnaligned(ref data.Values[offset + Unsafe.SizeOf<Channel<T>>()], value);
        data.ValueCount += size;
        var node = new GraphNode
        {
            Kind = GraphNodeKind.Track, Target = target, Spec = spec, ValueOffset = offset,
            Layer = layer, Endpoints = endpoints, Install = &GraphTracks.Install<T>,
            Field = LayerChannel.Of(channel),
        };
        for (int parent = data.Current; parent != 0; parent = data.Nodes![parent].Parent)
            if (data.Nodes![parent].Kind == GraphNodeKind.Layer)
            {
                node.Layer = data.Nodes[parent].Layer;
                node.LayerDefinition = data.Nodes[parent].LayerDefinition;
                node.DefinesLayer = true;
                break;
            }
        GraphBuilding.Add(_data, node);
    }

    public void Delay(float seconds)
    {
        DevelopmentCheck.Require(float.IsFinite(seconds) && seconds >= 0, "Graph delay must be finite and nonnegative.");
        GraphBuilding.Add(_data!, new GraphNode { Kind = GraphNodeKind.Delay, DelayDuration = seconds });
    }

    public BuilderScope Sequence() => GraphBuilding.Open(_data!, GraphNodeKind.Sequence);
    public BuilderScope Parallel() => GraphBuilding.Open(_data!, GraphNodeKind.Parallel);
    public BuilderScope Branch() => GraphBuilding.Open(_data!, GraphNodeKind.Branch);

    public BuilderScope Layer(byte layer, AnimationLayer definition)
    {
        DevelopmentCheck.Require(layer < 8, "Layer index must fit LayerArray.");
        DevelopmentCheck.Require(definition.Mode is LayerMode.Override or LayerMode.Additive &&
            float.IsFinite(definition.Weight) && definition.Weight >= 0 && definition.Weight <= 1,
            "A graph layer requires a defined blend mode and a weight between zero and one.");
        BuilderScope scope = GraphBuilding.Open(_data!, GraphNodeKind.Layer);
        _data![0].Nodes![scope.Id].Layer = layer;
        _data[0].Nodes![scope.Id].LayerDefinition = definition;
        return scope;
    }

    public StateMachineScope StateMachine(ref GraphParameters parameters)
    {
        _data![0].Parameters = parameters;
        return new StateMachineScope(_data);
    }

    public Blend1DScope Blend1D(byte parameter, ref GraphParameters parameters)
    {
        _data![0].Parameters = parameters;
        return new Blend1DScope(_data, parameter);
    }

    public Blend1DScope Blend1D(byte parameter) => new(_data!, parameter);

    public BuilderScope Repeat(int count)
    {
        DevelopmentCheck.Require(count > 0, "Graph repetition requires a positive count.");
        return GraphBuilding.Open(_data!, new GraphNode { Kind = GraphNodeKind.Repeat, Repetitions = count });
    }

    public void Boundary(StageEnd end)
    {
        DevelopmentCheck.Require(end is StageEnd.Duration or StageEnd.Settled or StageEnd.External,
            "Graph boundary requires a defined stage end.");
        GraphBuilding.Add(_data!, new GraphNode { Kind = GraphNodeKind.Boundary, End = end });
    }

    public BuilderScope Stagger(float interval)
    {
        DevelopmentCheck.Require(float.IsFinite(interval) && interval >= 0, "Stagger interval must be finite and nonnegative.");
        return GraphBuilding.Open(_data!, new GraphNode { Kind = GraphNodeKind.Stagger, StaggerInterval = interval });
    }

    public CompiledGraph Compile()
    {
        ref BuildData build = ref _data![0];
        DevelopmentCheck.Require(build.Current == 0, "Close graph scopes before compiling.");
        var data = ArrayPool<GraphData>.Shared.Rent(1);
        data[0] = new GraphData
        {
            Nodes = build.Nodes!, NodeCount = build.Count, Values = build.Values!, Parameters = build.Parameters, References = 1,
            CompileReference = 1, Version = GraphData.NextVersion(),
        };
        build.Nodes = null;
        build.Values = null;
        GraphBuilding.Initialize(_data);
        try { GraphCompiler.Compile(data); }
        catch
        {
            GraphData.Release(data);
            throw;
        }
        return new CompiledGraph(data);
    }

    public Entity Play()
    {
        using CompiledGraph graph = Compile();
        return graph.Play(_data![0].World);
    }

    public void Dispose()
    {
        if (_data is null)
            return;
        GraphArrays.Return(ref _data[0].Nodes);
        GraphArrays.Return(ref _data[0].Values);
        ArrayPool<BuildData>.Shared.Return(_data, clearArray: true);
        _data = null;
    }
}

public ref struct BuilderScope
{
    private BuildData[]? _data;
    private readonly int _node;

    internal BuilderScope(BuildData[] data, int node) => (_data, _node) = (data, node);
    public readonly int Id => _node;

    public void Dispose()
    {
        if (_data is null)
            return;
        ref BuildData data = ref _data[0];
        DevelopmentCheck.Require(data.Current == _node, "Close graph scopes in nesting order.");
        data.Current = data.Nodes![_node].Parent;
        _data = null;
    }
}

internal struct BuildData
{
    public World World;
    public GraphNode[]? Nodes;
    public byte[]? Values;
    public int Count, Current, ValueCount;
    public GraphParameters Parameters;
}

internal static class GraphBuilding
{
    public static void Initialize(BuildData[] data)
    {
        ref BuildData build = ref data[0];
        build.Nodes = ArrayPool<GraphNode>.Shared.Rent(32);
        build.Values = ArrayPool<byte>.Shared.Rent(256);
        build.Nodes[0] = new GraphNode { Kind = GraphNodeKind.Parallel };
        build.Count = 1;
        build.Current = build.ValueCount = 0;
    }

    public static int Add(BuildData[] data, GraphNode node)
    {
        ref BuildData build = ref data[0];
        GraphArrays.Grow(ref build.Nodes, build.Count + 1);
        int index = build.Count++;
        node.Parent = build.Current;
        ref GraphNode parent = ref build.Nodes![build.Current];
        if (parent.FirstChild == 0)
            parent.FirstChild = index;
        else
            build.Nodes[parent.LastChild].Next = index;
        parent.LastChild = index;
        build.Nodes[index] = node;
        return index;
    }

    public static BuilderScope Open(BuildData[] data, GraphNodeKind kind) => Open(data, new GraphNode { Kind = kind });

    public static BuilderScope Open(BuildData[] data, GraphNode definition)
    {
        int node = Add(data, definition);
        data[0].Current = node;
        return new BuilderScope(data, node);
    }
}

internal static class GraphArrays
{
    public static void Grow<T>(ref T[]? array, int count)
    {
        if (array is not null && array.Length >= count)
            return;
        T[] next = ArrayPool<T>.Shared.Rent(Math.Max(count, (array?.Length ?? 8) * 2));
        if (array is not null)
        {
            array.CopyTo(next, 0);
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
        array = next;
    }

    public static void Return<T>(ref T[]? array)
    {
        if (array is not null)
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        array = null;
    }
}
