using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

/// <summary>Immutable graph data that can start independent animation instances.</summary>
public unsafe struct CompiledGraph : IDisposable
{
    private GraphData[]? _data;
    private readonly int _version;
    internal CompiledGraph(GraphData[] data) => (_data, _version) = (data, data[0].Version);

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
                    if (!world.HasBuffer<GraphBranch>(root))
                        world.AddBuffer<GraphBranch>(root);
                    var branch = new GraphBranch { Id = data.Parts[part].Id, Animation = animation };
                    world.ExecuteBufferWrite<GraphBranch, GraphBranch>(root, ref branch,
                        static (DynamicBuffer<GraphBranch> branches, ref GraphBranch branch) => branches.Add(branch));
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
        if (world.HasBuffer<GraphBranch>(animation))
            world.ExecuteBufferWrite<GraphBranch>(animation, static branches => branches.Clear());
        Timeline previous = world.Read<Timeline>(animation);
        var timeline = new Timeline { Starts = previous.Starts + 1, Completions = previous.Completions };
        if (sharedTime != Entity.Null)
            timeline = world.Read<Timeline>(sharedTime);
        GraphPlayback.Start(world, animation, _data, part, sharedTime);
        if (!world.HasBuffer<TimelineStage>(animation))
            world.AddBuffer<TimelineStage>(animation);
        var stages = data.Parts![part];
        world.ExecuteBufferWrite<TimelineStage, GraphPart>(animation, ref stages,
            static (DynamicBuffer<TimelineStage> buffer, ref GraphPart stages) =>
            {
                buffer.Clear();
                for (int i = 0; i < stages.StageCount; i++)
                    buffer.Add(stages.Stages![i]);
            });
        for (int i = 0; i < data.TrackCount; i++)
        {
            GraphTrack track = data.Tracks![i];
            if (track.Part != part)
                continue;
            track.Node.Install(world, animation, in track, data.Values!, timeline.Starts, target);
        }
        world.Replace(animation, timeline);
        world.Enable<Timeline>(animation);
        return animation;
    }

    private static Entity RentEntity(World world)
    {
        QueryHandle query = world.Query(GraphQueries.Pool);
        Entity available = Entity.Null;
        try
        {
            var search = (World: world, Available: available);
            world.ExecuteQuery(query, ref search, static (QueryCursor rows, ref (World World, Entity Available) search) =>
            {
                foreach (QueryRow row in rows.Rows)
                    if (!search.World.IsEnabled<Timeline>(row.Entity))
                    {
                        search.Available = row.Entity;
                        break;
                    }
            });
            available = search.Available;
        }
        finally { world.ReleaseQuery(query); }
        if (available == Entity.Null)
        {
            available = world.CreateEntity(new Timeline());
            world.AddTag<PooledGraph>(available);
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
    public float DelayDuration, StaggerInterval, Coordinate, FadeDuration;
    public int Repetitions;
    public int ValueOffset;
    public byte Layer;
    public TrackEndpoints Endpoints;
    public StageEnd End;
    public int StateId, NextState;
    public byte Parameter;
    public bool Initial;
    public GraphCondition Condition;
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
    public int GroupCount, MotionCount, References, CompileReference, Version;
    public GraphParameters Parameters;
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
}

internal struct GraphMotionData
{
    public int Group, Node, StageCount;
    public TimelineStage[]? Stages;
}

internal struct GraphPart
{
    public int Id, TrackCount, StageCount, TimelinePart;
    public TimelineStage[]? Stages;
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
            AnimationLayers layers = exists ? world.Read<AnimationLayers>(target) : default;
            while (layers.Count <= source.Node.Layer)
                layers.Items[layers.Count++] = new AnimationLayer { Weight = 1 };
            layers.Items[source.Node.Layer] = source.Node.LayerDefinition;
            if (exists)
                world.Replace(target, layers);
            else
                world.Add(target, layers);
        }
        PropertyAnimation.Declare<T>(world, animation);
        var track = new Track<T>
        {
            Target = target == animation ? Entity.Null : target, Channel = channel, To = value,
            Duration = source.Node.Spec.Duration, Curve = source.Node.Spec.Curve, Layer = source.Node.Layer,
            Stage = source.Stage, Start = source.Start, SampleTime = source.Start, StartId = starts,
            Endpoints = source.Node.Endpoints, CaptureStart = true,
            GraphMotion = source.Motion, GraphGroup = source.Group, MotionStage = source.MotionStage,
            FromGraph = true,
        };
        var addition = (Track: track, Shared: false);
        world.ExecuteBufferWrite<Track<T>, (Track<T> Track, bool Shared)>(animation, ref addition,
            static (DynamicBuffer<Track<T>> tracks, ref (Track<T> Track, bool Shared) addition) =>
            {
                Track<T> track = addition.Track;
                if (tracks.Count > 0 && tracks.Read(0).StartId != track.StartId)
                    tracks.Clear();
                foreach (Track<T> other in tracks.AsSpan())
                    if (other.Target == track.Target && other.Channel == track.Channel &&
                        (other.Layer != track.Layer || other.GraphGroup != track.GraphGroup))
                        addition.Shared = true;
                tracks.Add(track);
            });
        if ((addition.Shared || HasOtherSource(world, animation, target, channel)) &&
            !world.HasBuffer<Contribution<T>>(target))
            world.AddBuffer<Contribution<T>>(target);
    }

    private static bool HasOtherSource<T>(World world, Entity animation, Entity target, Channel<T> channel) where T : unmanaged
    {
        QueryHandle query = world.Query(GraphTrackQueries<T>.Sources);
        var field = (Animation: animation, Target: target, Channel: channel, Shared: false);
        try
        {
            world.ExecuteQuery(query, ref field,
                static (QueryCursor rows, ref (Entity Animation, Entity Target, Channel<T> Channel, bool Shared) field) =>
                {
                    foreach (QueryRow row in rows.Rows)
                    {
                        if (row.Entity == field.Animation)
                            continue;
                        uint starts = row.Read<Timeline>().Starts;
                        foreach (Track<T> track in row.ReadBuffer<Track<T>>().AsSpan())
                        {
                            Entity target = track.Target == Entity.Null ? row.Entity : track.Target;
                            if (target == field.Target && track.Channel == field.Channel && track.StartId == starts &&
                                (!track.Finished || track.FromGraph))
                            {
                                field.Shared = true;
                                return;
                            }
                        }
                    }
                });
        }
        finally { world.ReleaseQuery(query); }
        return field.Shared;
    }
}

internal static class GraphTrackQueries<T> where T : unmanaged
{
    internal static readonly QueryDefinition Sources = new QueryDefinitionBuilder()
        .Read<Timeline>().Enabled<Timeline>().ReadBuffer<Track<T>>().Build();
}
