using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    /// <summary>Declares inline storage before animation starts.</summary>
    public static void Declare<T>(World world, Entity entity) where T : unmanaged
    {
        if (!DefaultProperties<T>.Declare(world, entity)) DeclareStorage<T>(world, entity);
    }

    internal static void DeclareStorage<T>(World world, Entity entity) where T : unmanaged
    {
        if (!world.Has<Timeline>(entity))
        {
            world.Add(entity, new Timeline());
            world.Disable<Timeline>(entity);
        }
        if (!world.HasBuffer<Track<T>>(entity))
            world.AddBuffer<Track<T>>(entity);
        _ = SourceQueries<T>.For(world);
    }

    public static Entity To<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer = 0)
        where T : unmanaged => StartCompatible(world, target, channel, value, spec, layer, TrackEndpoints.Values);

    /// <summary>Samples a displacement back toward the field value captured when playback starts.</summary>
    public static Entity By<T>(World world, Entity target, Channel<T> channel, T delta, Spec spec, byte layer = 0)
        where T : unmanaged => StartCompatible(world, target, channel, delta, spec, layer, TrackEndpoints.Displacement);

    private static Entity StartCompatible<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged => DefaultProperties<T>.TryStart(world, target, channel, value, spec, layer, endpoints, out Entity animation)
            ? animation : Start(world, target, channel, value, spec, layer, endpoints);

    private static Entity Start<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged
    {
        T current = channel.Read(world, target);
        bool hasLayers = channel.Component != ComponentId<AnimationLayers>.Value && world.Has<AnimationLayers>(target);
        if (hasLayers)
        {
            AnimationLayers layers = world.Read<AnimationLayers>(target);
            DevelopmentCheck.Require(layers.Count <= 8 && layer < layers.Count,
                "A property track requires a defined animation layer.");
            if (layers.Items[layer].Mode == LayerMode.Additive)
                ReadBase(world, target, channel, ref current);
        }
        bool inline = world.Has<Timeline>(target) && world.HasBuffer<Track<T>>(target);
        var matches = new FieldAnimations<T>
        {
            World = world, Target = target, Channel = channel, Layer = layer, Endpoints = endpoints,
            InlineTarget = inline ? target : Entity.Null,
        };
        QueryHandle query = world.Query(world.QueryDefinition()
            .Read<Timeline>().WriteBuffer<Track<T>>().Optional<PooledAnimation>());
        try
        {
            world.ExecuteQuery(query, ref matches, static (QueryCursor rows, ref FieldAnimations<T> matches) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    bool enabled = matches.World.IsEnabled<Timeline>(row.Entity);
                    bool holdsValues = false;
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    Timeline timeline = row.Read<Timeline>();
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        Track<T> track = tracks.Read(i);
                        bool holdsValue = HoldsLayerValue(matches.World, row.Entity, in track);
                        holdsValues |= holdsValue;
                        Entity target = track.Target == Entity.Null ? row.Entity : track.Target;
                        if ((track.Finished ? !holdsValue : !enabled) || target != matches.Target || track.Channel != matches.Channel ||
                            !holdsValue && (track.StartId != timeline.Starts || track.Stage != timeline.Stage))
                            continue;
                        if (track.Layer == matches.Layer && track.Endpoints == TrackEndpoints.Values && matches.Endpoints == TrackEndpoints.Values)
                        {
                            matches.ExistingAnimation = row.Entity;
                            matches.CurveKind = track.Curve.Kind;
                            matches.Velocity = track.Velocity;
                            matches.Current = track.Current;
                            if (matches.InlineTarget != Entity.Null && row.Entity != matches.InlineTarget)
                                tracks.SwapRemoveAt(i--);
                        }
                        else
                        {
                            matches.SharedField = true;
                        }
                    }
                    if (!enabled && !holdsValues && row.Has<PooledAnimation>() && matches.AvailableAnimation == Entity.Null)
                        matches.AvailableAnimation = row.Entity;
                }
            });
        }
        finally
        {
            world.ReleaseQuery(query);
        }

        if (matches.SharedField && !world.HasBuffer<Contribution<T>>(target))
            world.AddBuffer<Contribution<T>>(target);
        if (matches.ExistingAnimation != Entity.Null && (hasLayers || world.HasBuffer<Contribution<T>>(target)))
            current = matches.Current;

        Entity animation = target;
        if (!inline)
        {
            animation = matches.ExistingAnimation != Entity.Null ? matches.ExistingAnimation : matches.AvailableAnimation;
            if (animation == Entity.Null)
            {
                animation = world.CreateEntity(new Timeline());
                world.AddTag<PooledAnimation>(animation);
                world.AddBuffer<Track<T>>(animation);
                world.Disable<Timeline>(animation);
            }
            if (matches.ExistingAnimation == Entity.Null)
            {
                world.ExecuteBufferWrite<Track<T>>(animation, static tracks => tracks.Clear());
                Timeline previous = world.Read<Timeline>(animation);
                world.Replace(animation, new Timeline { Completions = previous.Completions, Starts = previous.Starts });
                if (world.HasBuffer<TimelineStage>(animation))
                    world.ExecuteBufferWrite<TimelineStage>(animation, static stages => stages.Clear());
            }
        }

        Timeline timeline = ReadStartingTimeline(world, animation);
        var track = new Track<T>
        {
            Target = animation == target ? Entity.Null : target,
            Channel = channel, From = current, To = value, Current = current,
            Duration = spec.Duration, Curve = spec.Curve, Layer = layer, Stage = timeline.Stage,
            Start = timeline.Time, SampleTime = timeline.Time,
            StartId = timeline.Starts,
            Endpoints = endpoints,
            Velocity = endpoints == TrackEndpoints.Values && matches.CurveKind == CurveKind.Spring && spec.Curve.Kind == CurveKind.Spring
                ? matches.Velocity : default,
        };
        world.ExecuteBufferWrite<Track<T>, Track<T>>(animation, ref track,
            static (DynamicBuffer<Track<T>> tracks, ref Track<T> track) =>
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    Track<T> previous = tracks.Read(i);
                    if (previous.Target != track.Target || previous.Channel != track.Channel || previous.Layer != track.Layer)
                        continue;
                    if (!previous.Finished && previous.StartId == track.StartId &&
                        (previous.Stage != track.Stage || previous.Endpoints != TrackEndpoints.Values || track.Endpoints != TrackEndpoints.Values))
                        continue;
                    tracks[i] = track;
                    return;
                }
                tracks.Add(track);
            });
        world.Replace(animation, timeline);
        world.Enable<Timeline>(animation);
        return animation;
    }

    internal static bool HoldsLayerValue<T>(World world, Entity animation, in Track<T> track) where T : unmanaged
    {
        if (track.Typed && !track.SourceActive || !track.Finished || track.FromGraph || track.GraphMotion != 0 || track.Endpoints != TrackEndpoints.Values ||
            track.Channel.Component == ComponentId<AnimationLayers>.Value)
            return false;
        Entity target = track.Target == Entity.Null ? animation : track.Target;
        return world.IsAlive(target) && world.Has<AnimationLayers>(target);
    }

    /// <summary>Retargets the current stage's active channel with its existing curve and duration.</summary>
    public static unsafe void Retarget<T>(World world, Entity animation, Channel<T> channel, T value) where T : unmanaged
    {
        DevelopmentCheck.Require(world.IsEnabled<Timeline>(animation), "Retarget requires an enabled animation.");
        Timeline timeline = world.Read<Timeline>(animation);
        bool found = false;
        int count = BufferCount<Track<T>>(world, animation);
        for (int index = 0; index < count; index++)
        {
            Track<T> track = ReadTrack<T>(world, animation, index);
            if (track.Channel != channel || track.Finished || track.StartId != timeline.Starts || track.Stage != timeline.Stage) continue;
            Entity target = track.Target == Entity.Null ? animation : track.Target;
            T current = track.Typed || world.Has<AnimationLayers>(target) || world.HasBuffer<Contribution<T>>(target)
                ? track.Current : track.Channel.Read(world, target);
            track.From = track.Current = current;
            track.To = value;
            track.Endpoints = TrackEndpoints.Values;
            track.Start = track.SampleTime = timeline.Time;
            if (track.Typed)
            {
                var values = new MotionValues<T>(current, value, current);
                MotionAccesses<T>.Items[track.MotionType - 1].Retarget(world, animation, track.MotionIndex, in values);
                UpdateDirectBoundary(world, animation, index, in track, in timeline, new(track.End, track.Duration));
            }
            WriteTrack(world, animation, index, in track);
            found = true;
        }
        DevelopmentCheck.Require(found, "Retarget requires a matching channel in the animation's track buffer.");
    }

    /// <summary>Stops playback at the currently presented values.</summary>
    public static unsafe void Stop(World world, Entity animation)
    {
        if (world.HasBuffer<MotionStop>(animation))
        {
            int count = BufferCount<MotionStop>(world, animation);
            for (int index = 0; index < count; index++) ReadBufferItem<MotionStop>(world, animation, index).Stop(world, animation);
        }
        world.Disable<Timeline>(animation);
    }

    public static void Pause(World world, Entity animation)
    {
        Timeline timeline = world.Read<Timeline>(animation);
        timeline.Paused = true;
        world.Replace(animation, timeline);
    }

    public static void Resume(World world, Entity animation)
    {
        Timeline timeline = world.Read<Timeline>(animation);
        timeline.Paused = false;
        world.Replace(animation, timeline);
    }

    public static void SetSpeed(World world, Entity animation, float speed)
    {
        DevelopmentCheck.Require(float.IsFinite(speed) && speed >= 0, "Animation speed must be finite and nonnegative.");
        Timeline timeline = world.Read<Timeline>(animation);
        timeline.Speed = speed;
        world.Replace(animation, timeline);
    }

    private static Timeline ReadStartingTimeline(World world, Entity animation)
    {
        Timeline timeline = world.Read<Timeline>(animation);
        if (!world.IsEnabled<Timeline>(animation))
        {
            timeline.Time = 0;
            timeline.Stage = 0;
            timeline.Running = 0;
            timeline.Paused = false;
            timeline.Starts++;
        }
        return timeline;
    }

    private struct PooledAnimation : ITag;

    private struct FieldAnimations<T> where T : unmanaged
    {
        public World World;
        public Entity Target;
        public Channel<T> Channel;
        public byte Layer;
        public TrackEndpoints Endpoints;
        public Entity InlineTarget, ExistingAnimation, AvailableAnimation;
        public bool SharedField;
        public CurveKind CurveKind;
        public T Velocity;
        public T Current;
    }
}
