using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

public static class PropertyAnimation
{
    /// <summary>Declares inline storage before animation starts.</summary>
    public static void Declare<T>(World world, Entity entity) where T : unmanaged
    {
        if (!world.Has<Timeline>(entity))
        {
            world.Add(entity, new Timeline());
            world.Disable<Timeline>(entity);
        }
        if (!world.HasBuffer<Track<T>>(entity))
            world.AddBuffer<Track<T>>(entity);
    }

    public static Entity To<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer = 0)
        where T : unmanaged => Start(world, target, channel, value, spec, layer, TrackEndpoints.Values);

    /// <summary>Samples a displacement back toward the field value captured when playback starts.</summary>
    public static Entity By<T>(World world, Entity target, Channel<T> channel, T delta, Spec spec, byte layer = 0)
        where T : unmanaged => Start(world, target, channel, delta, spec, layer, TrackEndpoints.Displacement);

    private static Entity Start<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged
    {
        T current = Unsafe.ReadUnaligned<T>(in world.ReadBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>()));
        bool inline = world.Has<Timeline>(target) && world.HasBuffer<Track<T>>(target);
        var matches = new FieldAnimations<T>
        {
            World = world, Target = target, Channel = channel, Layer = layer,
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
                    if (!matches.World.IsEnabled<Timeline>(row.Entity))
                    {
                        if (row.Has<PooledAnimation>() && matches.AvailableAnimation == Entity.Null)
                            matches.AvailableAnimation = row.Entity;
                        continue;
                    }
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    Timeline timeline = row.Read<Timeline>();
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        Track<T> track = tracks.Read(i);
                        Entity target = track.Target == Entity.Null ? row.Entity : track.Target;
                        if (track.Finished || target != matches.Target || track.Channel != matches.Channel || track.StartId != timeline.Starts || track.Stage != timeline.Stage)
                            continue;
                        if (track.Layer == matches.Layer)
                        {
                            matches.ExistingAnimation = row.Entity;
                            matches.CurveKind = track.Curve.Kind;
                            matches.Velocity = track.Velocity;
                            if (matches.InlineTarget != Entity.Null && row.Entity != matches.InlineTarget)
                                tracks[i].Finished = true;
                        }
                        else
                        {
                            tracks[i].Finished = true;
                            matches.SharedField = true;
                        }
                    }
                }
            });
        }
        finally
        {
            world.ReleaseQuery(query);
        }

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
                    tracks[i] = track;
                    return;
                }
                tracks.Add(track);
            });
        world.Replace(animation, timeline);
        world.Enable<Timeline>(animation);
        DevelopmentCheck.Require(!matches.SharedField, "Multiple animation sources share a field; the latest source replaces the earlier source.");
        return animation;
    }

    /// <summary>Retargets the current stage's active channel with its existing curve and duration.</summary>
    public static void Retarget<T>(World world, Entity animation, Channel<T> channel, T value) where T : unmanaged
    {
        DevelopmentCheck.Require(world.IsEnabled<Timeline>(animation), "Retarget requires an enabled animation.");
        Timeline timeline = world.Read<Timeline>(animation);
        var destination = new TrackDestination<T>
        {
            World = world, Animation = animation, Channel = channel, Value = value, Timeline = timeline,
        };
        world.ExecuteBufferWrite<Track<T>, TrackDestination<T>>(animation, ref destination,
            static (DynamicBuffer<Track<T>> tracks, ref TrackDestination<T> destination) =>
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    Track<T> previous = tracks.Read(i);
                    if (previous.Channel != destination.Channel || previous.Finished || previous.StartId != destination.Timeline.Starts || previous.Stage != destination.Timeline.Stage)
                        continue;
                    ref Track<T> track = ref tracks[i];
                    Entity target = track.Target == Entity.Null ? destination.Animation : track.Target;
                    T current = Unsafe.ReadUnaligned<T>(in destination.World.ReadBytes(target, track.Channel.Component, track.Channel.Offset, Unsafe.SizeOf<T>()));
                    track.From = track.Current = current;
                    track.To = destination.Value;
                    track.Endpoints = TrackEndpoints.Values;
                    track.Start = track.SampleTime = destination.Timeline.Time;
                    destination.Found = true;
                }
            });
        DevelopmentCheck.Require(destination.Found, "Retarget requires a matching channel in the animation's track buffer.");
    }

    /// <summary>Stops playback at the currently presented values.</summary>
    public static void Stop(World world, Entity animation) => world.Disable<Timeline>(animation);

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

    private struct TrackDestination<T> where T : unmanaged
    {
        public World World;
        public Entity Animation;
        public Channel<T> Channel;
        public T Value;
        public Timeline Timeline;
        public bool Found;
    }

    private struct FieldAnimations<T> where T : unmanaged
    {
        public World World;
        public Entity Target;
        public Channel<T> Channel;
        public byte Layer;
        public Entity InlineTarget, ExistingAnimation, AvailableAnimation;
        public bool SharedField;
        public CurveKind CurveKind;
        public T Velocity;
    }
}
