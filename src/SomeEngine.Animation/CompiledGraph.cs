using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>Immutable graph data that can start independent animation instances.</summary>
public unsafe struct CompiledGraph : IDisposable
{
    private GraphData[]? _data;
    private readonly int _version;
    internal CompiledGraph(GraphData[] data) => (_data, _version) = (data, data[0].Version);

    public ReadOnlySpan<QueryDescription> Inputs => _data![0].Inputs;

    public Entity Play(World world, Entity target = default)
    {
        DevelopmentCheck.Require(_data is not null && _data[0].Version == _version && _data[0].CompileReference == 1,
            "Play requires a live compiled graph handle.");
        ref GraphData data = ref _data![0];
        Entity root = Entity.Null;
        Entity[] animations = ArrayPool<Entity>.Shared.Rent(data.PartCount);
        try
        {
            for (int part = 0; part < data.PartCount; part++)
            {
                int timelinePart = data.Parts![part].TimelinePart;
                Entity sharedTime = timelinePart == part ? Entity.Null : animations[timelinePart];
                Entity animation = PlayPart(world, part, target, sharedTime);
                animations[part] = animation;
                if (part == 0)
                    root = animation;
                else
                {
                    if (!world.Has<Buffer<GraphBranch>>(root))
                        world.AddBuffer<GraphBranch>(root);
                    var branch = new GraphBranch { Id = data.Parts[part].Id, Animation = animation };
                    {
                        using var branches = world.GetBuffer<GraphBranch>(root);
                        branches.Add(branch);
                    }
                }
            }
        }
        finally { ArrayPool<Entity>.Shared.Return(animations); }
        return root;
    }

    private Entity PlayPart(World world, int part, Entity target, Entity sharedTime)
    {
        ref GraphData data = ref _data![0];
        Entity inline = Entity.Null;
        bool multiple = false;
        for (int i = 0; i < data.TrackCount; i++)
        {
            GraphTrack track = data.Tracks![i];
            if (track.Part != part)
                continue;
            Entity output = target == Entity.Null ? track.Node.Target : target;
            if (inline == Entity.Null)
                inline = output;
            else if (inline != output)
                multiple = true;
        }
        if (multiple || inline != Entity.Null && world.Has<Timeline>(inline) && world.IsEnabled<Timeline>(inline))
            inline = Entity.Null;
        Entity animation = inline == Entity.Null ? RentEntity(world) : inline;
        if (!world.Has<Timeline>(animation))
            world.Add(animation, new Timeline());
        if (world.Has<Buffer<GraphBranch>>(animation))
        {
            using var branches = world.GetBuffer<GraphBranch>(animation);
            branches.Clear();
        }
        Timeline previous = world.ReadComponentData<Timeline>(animation);
        PropertyAnimation.Stop(world, animation);
        var timeline = new Timeline { Starts = previous.Starts + 1, Completions = previous.Completions };
        if (sharedTime != Entity.Null)
            timeline = world.ReadComponentData<Timeline>(sharedTime);
        world.Set(animation, timeline);
        GraphPlayback.Start(world, animation, _data, part, sharedTime);
        if (!world.Has<Buffer<TimelineStage>>(animation))
            world.AddBuffer<TimelineStage>(animation);
        var stages = data.Parts![part];
        {
            using var buffer = world.GetBuffer<TimelineStage>(animation);
            buffer.Clear();
            for (int i = 0; i < stages.StageCount; i++)
                buffer.Add(stages.Stages![i]);
        }
        for (int i = 0; i < data.TrackCount; i++)
        {
            GraphTrack track = data.Tracks![i];
            if (track.Part != part)
                continue;
            track.Node.Install(world, animation, in track, data.Values!, timeline.Starts, target);
        }
        var leaves = new HashSet<Type>();
        for (int i = 0; i < data.TrackCount; i++)
        {
            GraphTrack track = data.Tracks![i];
            if (track.Part == part && track.Node.Leaf is { } leaf && leaves.Add(leaf.Type))
                leaf.Complete(world, animation);
        }
        world.Set(animation, timeline);
        world.Enable<Timeline>(animation);
        return animation;
    }

    private static Entity RentEntity(World world)
    {
        Query query = world.Query(GraphQueries.Pool);
        Entity available = Entity.Null;
        try
        {
            var search = (World: world, Available: available);
            {
                using QueryScope read = query.Open();
                Visit(in read, ref search);

                static void Visit(scoped in QueryScope rows, ref (World World, Entity Available) search)
                {
                    foreach (QueryChunk chunk in rows)
                        for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                            if (!search.World.IsEnabled<Timeline>(chunk.Entities[rowIndex]))
                            {
                                search.Available = chunk.Entities[rowIndex];
                                break;
                            }

                }
            }
            available = search.Available;
        }
        finally { }
        if (available == Entity.Null)
        {
            available = world.Create(new Timeline());
            world.Add(available, default(PooledGraph));
        }
        return available;
    }

    public void Dispose()
    {
        if (_data is null)
            return;
        if (_data[0].Version == _version && Interlocked.Exchange(ref _data[0].CompileReference, 0) == 1)
            GraphData.Release(_data);
        _data = null;
    }
}

internal struct PooledGraph : ITag;

internal enum GraphNodeKind : byte { Track, Delay, Sequence, Parallel, Stagger, Repeat, Boundary, Branch, StateMachine, State, Transition, Blend1D, BlendPoint, Layer }

internal unsafe struct GraphNode
{
    public GraphNodeKind Kind;
    public int Parent, FirstChild, LastChild, Next;
    public Entity Target;
    public Spec Spec;
    public MotionBoundary Boundary;
    public GraphLeafData? Leaf;
    public float DelayDuration, StaggerInterval, Coordinate, FadeDuration;
    public int Repetitions;
    public int ValueOffset;
    public byte Layer;
    public TrackEndpoints Endpoints;
    public StageEnd End;
    public int StateId, NextState;
    public byte Parameter;
    public bool Initial;
    public GraphConditionData? ConditionData;
    public int ConditionIndex;
    public delegate* managed<World, Entity, in GraphNode, in GraphParameters, bool> Matches;
    public LayerChannel Field;
    public AnimationLayer LayerDefinition;
    public bool DefinesLayer;
    public delegate*<World, Entity, in GraphTrack, byte[], uint, Entity, void> Install;
}

internal struct GraphTrack
{
    public GraphNode Node;
    public ushort Stage;
    public float Start;
    public int Part, TimelinePart;
    public int Motion, Group;
    public ushort MotionStage;
    public object? LeafBatch;
    public int LeafIndex, Output, Order;
}

internal struct GraphData
{
    public GraphNode[]? Nodes;
    public byte[]? Values;
    public GraphTrack[]? Tracks;
    public GraphPart[]? Parts;
    public int NodeCount, TrackCount, PartCount;
    public GraphGroupData[]? Groups;
    public GraphMotionData[]? Motions;
    public int[]? GroupMotions, TransitionNodes, BlendMotions, PartGroups;
    public int GroupCount, MotionCount, References, CompileReference, Version;
    public GraphParameters Parameters;
    public QueryDescription[]? Inputs;
    private static int _nextVersion;
    public static int NextVersion() => Interlocked.Increment(ref _nextVersion);

    public static void Release(GraphData[] data)
    {
        if (Interlocked.Decrement(ref data[0].References) != 0)
            return;
        GraphArrays.Return(ref data[0].Nodes);
        GraphArrays.Return(ref data[0].Values);
        GraphArrays.Return(ref data[0].Tracks);
        for (int part = 0; part < data[0].PartCount; part++)
            if (data[0].Parts![part].TimelinePart == part)
                GraphArrays.Return(ref data[0].Parts![part].Stages);
        for (int motion = 0; motion < data[0].MotionCount; motion++)
            GraphArrays.Return(ref data[0].Motions![motion].Stages);
        GraphArrays.Return(ref data[0].Parts);
        GraphArrays.Return(ref data[0].Groups);
        GraphArrays.Return(ref data[0].Motions);
        ArrayPool<GraphData>.Shared.Return(data, clearArray: true);
    }
}

internal struct GraphGroupData
{
    public int Node, Part, ParentMotion, OutputGroup;
    public ushort Stage;
    public float Start;
    public int MotionFirst, MotionCount, BlendFirst, BlendCount;
}

internal struct GraphMotionData
{
    public int Group, Node, StageCount;
    public TimelineStage[]? Stages;
    public int TransitionFirst, TransitionCount;
}

internal struct GraphPart
{
    public int Id, TrackCount, StageCount, TimelinePart;
    public TimelineStage[]? Stages;
    public int TrackFirst, GroupFirst, GroupCount;
}

internal static class GraphTracks
{
    public static void Install<T>(World world, Entity animation, in GraphTrack source, byte[] values, uint starts, Entity replacement)
        where T : unmanaged
    {
        Entity target = replacement == Entity.Null ? source.Node.Target : replacement;
        Channel<T> channel = Unsafe.ReadUnaligned<Channel<T>>(ref values[source.Node.ValueOffset]);
        T value = Unsafe.ReadUnaligned<T>(ref values[source.Node.ValueOffset + Unsafe.SizeOf<Channel<T>>()]);
        if (source.Node.DefinesLayer)
        {
            bool exists = world.Has<AnimationLayers>(target);
            AnimationLayers layers = exists ? world.ReadComponentData<AnimationLayers>(target) : default;
            while (layers.Count <= source.Node.Layer)
                layers.Items[layers.Count++] = new AnimationLayer { Weight = 1 };
            layers.Items[source.Node.Layer] = source.Node.LayerDefinition;
            if (exists)
                world.Set(target, layers);
            else
                world.Add(target, layers);
        }
        PropertyAnimation.Declare<T>(world, animation);
        var track = new Track<T>
        {
            Target = target == animation ? Entity.Null : target,
            Channel = channel,
            To = value,
            Duration = source.Node.Spec.Duration,
            Curve = source.Node.Spec.Curve,
            Layer = source.Node.Layer,
            Stage = source.Stage,
            Start = source.Start,
            SampleTime = source.Start,
            StartId = starts,
            Endpoints = source.Node.Endpoints,
            CaptureStart = true,
            GraphMotion = source.Motion,
            GraphGroup = source.Group,
            MotionStage = source.MotionStage,
            GraphOutput = source.Output,
            FromGraph = true,
        };
        var addition = (Track: track, Shared: false);
        {
            using var tracks = world.GetBuffer<Track<T>>(animation);
            Track<T> additionTrack = addition.Track;
            if (tracks.Count > 0 && tracks[0].StartId != additionTrack.StartId)
                tracks.Clear();
            foreach (Track<T> other in tracks.AsSpan())
                if (other.Target == additionTrack.Target && other.Channel == additionTrack.Channel &&
                    (other.Layer != additionTrack.Layer || other.GraphGroup != additionTrack.GraphGroup))
                    addition.Shared = true;
            tracks.Add(track);
        }
        if ((addition.Shared || HasOtherSource(world, animation, target, channel)) &&
            !world.Has<Buffer<Contribution<T>>>(target))
            world.AddBuffer<Contribution<T>>(target);
    }

    private static bool HasOtherSource<T>(World world, Entity animation, Entity target, Channel<T> channel) where T : unmanaged
    {
        Query query = world.Query(GraphTrackQueries<T>.Sources);
        var field = (Animation: animation, Target: target, Channel: channel, Shared: false);
        try
        {
            {
                using QueryScope read = query.Open();
                Visit(in read, ref field);

                static void Visit(scoped in QueryScope rows, ref (Entity Animation, Entity Target, Channel<T> Channel, bool Shared) field)
                {
                    foreach (QueryChunk chunk in rows)
                    {
                        chunk.TryRead<Timeline>(out var values0);
                        for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                        {
                            if (chunk.Entities[rowIndex] == field.Animation)
                                continue;
                            uint starts = values0[rowIndex].Starts;
                            foreach (Track<T> track in chunk.ReadBuffer<Track<T>>(rowIndex).AsSpan())
                            {
                                Entity target = track.Target == Entity.Null ? chunk.Entities[rowIndex] : track.Target;
                                if (target == field.Target && track.Channel == field.Channel && track.StartId == starts &&
                                    (!track.Finished || track.FromGraph))
                                {
                                    field.Shared = true;
                                    return;
                                }
                            }

                        }
                    }

                }
            }
        }
        finally { }
        return field.Shared;
    }
}

internal static class GraphTrackQueries<T> where T : unmanaged
{
    internal static readonly QueryDescription Sources = new QueryDescription()
        .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<Track<T>>>();
}
