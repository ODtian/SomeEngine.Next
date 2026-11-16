using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

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
        if (!world.Has<Buffer<Track<T>>>(entity))
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
            AnimationLayers layers = world.ReadValue<AnimationLayers>(target);
            DevelopmentCheck.Require(layers.Count <= 8 && layer < layers.Count,
                "A property track requires a defined animation layer.");
            if (layers.Items[layer].Mode == LayerMode.Additive)
                ReadBase(world, target, channel, ref current);
        }
        bool inline = world.Has<Timeline>(target) && world.Has<Buffer<Track<T>>>(target);
        var matches = new FieldAnimations<T>
        {
            World = world,
            Target = target,
            Channel = channel,
            Layer = layer,
            Endpoints = endpoints,
            InlineTarget = inline ? target : Entity.Null,
        };
        Query query = world.Query(new QueryDescription()
            .WithStored<Timeline>().WithStored<Buffer<Track<T>>>().Optional<PooledAnimation>());
        try
        {
            {
                using QueryScope read = query.Open();
                Visit(in read, ref matches);

                static void Visit(scoped in QueryScope rows, ref FieldAnimations<T> matches)
                {
                    foreach (QueryChunk chunk in rows)
                    {
                        chunk.TryRead<Timeline>(out var values0);
                        var buffers0 = chunk.Has<Buffer<Track<T>>>() ? chunk.GetBuffers<Track<T>>() : default;
                        for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                        {
                            bool enabled = matches.World.IsEnabled<Timeline>(chunk.Entities[rowIndex]);
                            bool holdsValues = false;
                            DynamicBuffer<Track<T>> tracks = buffers0[rowIndex];
                            Timeline timeline = values0[rowIndex];
                            for (int i = 0; i < tracks.Count; i++)
                            {
                                Track<T> track = tracks[i];
                                bool holdsValue = HoldsLayerValue(matches.World, chunk.Entities[rowIndex], in track);
                                holdsValues |= holdsValue;
                                Entity target = track.Target == Entity.Null ? chunk.Entities[rowIndex] : track.Target;
                                if ((track.Finished ? !holdsValue : !enabled) || target != matches.Target || track.Channel != matches.Channel ||
                                    !holdsValue && (track.StartId != timeline.Starts || track.Stage != timeline.Stage))
                                    continue;
                                if (track.Layer == matches.Layer && track.Endpoints == TrackEndpoints.Values && matches.Endpoints == TrackEndpoints.Values)
                                {
                                    matches.ExistingAnimation = chunk.Entities[rowIndex];
                                    matches.Track = track;
                                    if (matches.InlineTarget != Entity.Null && chunk.Entities[rowIndex] != matches.InlineTarget)
                                        tracks.RemoveAtSwapBack(i--);
                                }
                                else
                                {
                                    matches.SharedField = true;
                                }
                            }
                            if (!enabled && !holdsValues && chunk.Has<PooledAnimation>(rowIndex) && matches.AvailableAnimation == Entity.Null)
                                matches.AvailableAnimation = chunk.Entities[rowIndex];

                        }
                    }

                }
            }
        }
        finally
        {

        }

        if (matches.SharedField && !world.Has<Buffer<Contribution<T>>>(target))
            world.AddBuffer<Contribution<T>>(target);
        bool composed = hasLayers || world.Has<Buffer<Contribution<T>>>(target);
        if (matches.ExistingAnimation != Entity.Null && (!inline || matches.ExistingAnimation == target) &&
            (composed || EqualityComparer<T>.Default.Equals(matches.Track.Current, current)) &&
            EqualityComparer<T>.Default.Equals(matches.Track.To, value) && matches.Track.Duration == spec.Duration &&
            SameCurve(in matches.Track.Curve, in spec.Curve))
            return matches.ExistingAnimation;

        if (matches.ExistingAnimation != Entity.Null && composed)
            current = matches.Track.Current;

        Entity animation = target;
        if (!inline)
        {
            animation = matches.ExistingAnimation != Entity.Null ? matches.ExistingAnimation : matches.AvailableAnimation;
            if (animation == Entity.Null)
            {
                animation = world.Create(new Timeline());
                world.Add(animation, default(PooledAnimation));
                world.AddBuffer<Track<T>>(animation);
                world.Disable<Timeline>(animation);
            }
            if (matches.ExistingAnimation == Entity.Null)
            {
                {
                    using var tracks = world.GetBuffer<Track<T>>(animation);
                    tracks.Clear();
                }
                Timeline previous = world.ReadValue<Timeline>(animation);
                world.Set(animation, new Timeline { Completions = previous.Completions, Starts = previous.Starts });
                if (world.Has<Buffer<TimelineStage>>(animation))
                {
                    using var stages = world.GetBuffer<TimelineStage>(animation);
                    stages.Clear();
                }
            }
        }

        Timeline timeline = ReadStartingTimeline(world, animation);
        var track = new Track<T>
        {
            Target = animation == target ? Entity.Null : target,
            Channel = channel,
            From = current,
            To = value,
            Current = current,
            Duration = spec.Duration,
            Curve = spec.Curve,
            Layer = layer,
            Stage = timeline.Stage,
            Start = timeline.Time,
            SampleTime = timeline.Time,
            StartId = timeline.Starts,
            Endpoints = endpoints,
            Velocity = endpoints == TrackEndpoints.Values && matches.Track.Curve.Kind == CurveKind.Spring && spec.Curve.Kind == CurveKind.Spring
                ? matches.Track.Velocity : default,
        };
        {
            using var tracks = world.GetBuffer<Track<T>>(animation);
            Visit(tracks, ref track);

            static void Visit(DynamicBuffer<Track<T>> tracks, ref Track<T> track)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    Track<T> previous = tracks[i];
                    if (previous.Target != track.Target || previous.Channel != track.Channel || previous.Layer != track.Layer)
                        continue;
                    if (!previous.Finished && previous.StartId == track.StartId &&
                        (previous.Stage != track.Stage || previous.Endpoints != TrackEndpoints.Values || track.Endpoints != TrackEndpoints.Values))
                        continue;
                    tracks[i] = track;
                    return;
                }
                tracks.Add(track);

            }
        }
        world.Set(animation, timeline);
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
        Timeline timeline = world.ReadValue<Timeline>(animation);
        bool found = false;
        int count = BufferCount<Track<T>>(world, animation);
        for (int index = 0; index < count; index++)
        {
            Track<T> track = ReadTrack<T>(world, animation, index);
            if (track.Channel != channel || track.Finished || track.StartId != timeline.Starts || track.Stage != timeline.Stage) continue;
            if (track.FromGraph && (track.CaptureStart || !track.Sampled)) continue;
            float time = timeline.Time;
            if (track.GraphMotion > 0)
            {
                GraphMotion motion = ReadBufferItem<GraphMotion>(world, animation, track.GraphMotion - 1);
                if (motion.Weight <= 0 || track.MotionStage != motion.Stage) continue;
                time = motion.Time;
            }
            Entity target = track.Target == Entity.Null ? animation : track.Target;
            T current = track.Typed || world.Has<AnimationLayers>(target) || world.Has<Buffer<Contribution<T>>>(target)
                ? track.Current : track.Channel.Read(world, target);
            track.From = track.Current = current;
            track.To = value;
            track.Endpoints = TrackEndpoints.Values;
            track.SampleTime = time;
            if (track.FromGraph)
            {
                track.TimeOffset = time - track.Start;
                track.TimeScale = track.End == StageEnd.Duration ? track.Duration / (track.Duration - track.TimeOffset) : 1;
            }
            else
                track.Start = time;
            if (track.Typed)
            {
                var values = new MotionValues<T>(current, value, current);
                MotionAccesses<T>.Items[track.MotionType - 1].Retarget(world, animation, track.MotionIndex, in values);
                if (!track.FromGraph)
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
        if (world.Has<Buffer<MotionStop>>(animation))
        {
            int count = BufferCount<MotionStop>(world, animation);
            for (int index = 0; index < count; index++) ReadBufferItem<MotionStop>(world, animation, index).Stop(world, animation);
        }
        GraphReleases.Release(world, animation);
        world.Disable<Timeline>(animation);
    }

    public static void Pause(World world, Entity animation)
    {
        Timeline timeline = world.ReadValue<Timeline>(animation);
        timeline.Paused = true;
        world.Set(animation, timeline);
    }

    public static void Resume(World world, Entity animation)
    {
        Timeline timeline = world.ReadValue<Timeline>(animation);
        timeline.Paused = false;
        world.Set(animation, timeline);
    }

    public static void SetSpeed(World world, Entity animation, float speed)
    {
        DevelopmentCheck.Require(float.IsFinite(speed) && speed >= 0, "Animation speed must be finite and nonnegative.");
        Timeline timeline = world.ReadValue<Timeline>(animation);
        timeline.Speed = speed;
        world.Set(animation, timeline);
    }

    private static Timeline ReadStartingTimeline(World world, Entity animation)
    {
        Timeline timeline = world.ReadValue<Timeline>(animation);
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

    private static unsafe bool SameCurve(in Curve left, in Curve right) =>
        left.Kind == right.Kind && left.Easing == right.Easing && left.Stiffness == right.Stiffness &&
        left.Damping == right.Damping && left.Tolerance == right.Tolerance &&
        left.KeyframeStart == right.KeyframeStart && left.KeyframeCount == right.KeyframeCount &&
        (nint)left.Function == (nint)right.Function;

    private struct FieldAnimations<T> where T : unmanaged
    {
        public World World;
        public Entity Target;
        public Channel<T> Channel;
        public byte Layer;
        public TrackEndpoints Endpoints;
        public Entity InlineTarget, ExistingAnimation, AvailableAnimation;
        public bool SharedField;
        public Track<T> Track;
    }
}
