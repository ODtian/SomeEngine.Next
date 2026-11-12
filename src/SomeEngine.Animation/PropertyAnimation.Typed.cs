using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    /// <summary>Reserves the complete concrete algorithm and source capacity on a target.</summary>
    public static void Declare<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target,
        Channel<T> channel, Spec<T, TOps, TParameters, TState, TEvaluator> spec, int capacity = 1, byte layer = 0)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> =>
        DeclareTyped(world, target, target, channel, spec, capacity, layer);

    private static unsafe void DeclareTyped<T, TOps, TParameters, TState, TEvaluator>(World world, Entity animation, Entity target,
        Channel<T> channel, Spec<T, TOps, TParameters, TState, TEvaluator> spec, int capacity, byte layer)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        DevelopmentCheck.Require(capacity > 0, "A declared animation requires positive source capacity.");
        DeclareStorage<T>(world, animation);
        if (!world.Has<Buffer<PropertySource<T>>>(target)) world.AddBuffer<PropertySource<T>>(target);
        if (!world.Has<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>(animation))
            world.AddBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
        if (!world.Has<Buffer<MotionDeclaration<T>>>(animation)) world.AddBuffer<MotionDeclaration<T>>(animation);
        if (!world.Has<Buffer<MotionStop>>(animation)) world.AddBuffer<MotionStop>(animation);
        if (!world.Has<Buffer<DirectBoundary>>(animation)) world.AddBuffer<DirectBoundary>(animation);
        if (!world.Has<Buffer<TimelineStage>>(animation)) world.AddBuffer<TimelineStage>(animation);
        var reservation = (Declaration: new MotionDeclaration<T>
        {
            Channel = channel,
            Layer = layer,
            Capacity = capacity,
            Type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type
        }, Additional: capacity);
        {
            using var declarations = world.GetBuffer<MotionDeclaration<T>>(animation);
            Visit(declarations, ref reservation);

            static void Visit(DynamicBuffer<MotionDeclaration<T>> declarations, ref (MotionDeclaration<T> Declaration, int Additional) reservation)
            {
                for (int index = 0; index < declarations.Count; index++)
                {
                    MotionDeclaration<T> previous = declarations[index];
                    if (previous.Channel != reservation.Declaration.Channel || previous.Layer != reservation.Declaration.Layer || previous.Type != reservation.Declaration.Type) continue;
                    reservation.Additional = Math.Max(0, reservation.Declaration.Capacity - previous.Capacity);
                    if (reservation.Additional > 0) declarations[index] = reservation.Declaration;
                    return;
                }
                declarations.Add(reservation.Declaration);

            }
        }
        int additional = reservation.Additional;
        {
            using var instances = world.GetBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
            int count = instances.Count + additional;
            instances.EnsureCapacity(count);
            while (instances.Count < count) instances.Add(default);
        }
        var stop = new MotionStop
        {
            Type = reservation.Declaration.Type,
            ValueType = ComponentId<Buffer<Track<T>>>.Value,
            Stop = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.StopFunction
        };
        {
            using var stops = world.GetBuffer<MotionStop>(animation);
            Visit(stops, ref stop);

            static void Visit(DynamicBuffer<MotionStop> stops, ref MotionStop stop)
            {
                foreach (MotionStop previous in stops.AsSpan()) if (previous.Type == stop.Type && previous.ValueType == stop.ValueType) return;
                stops.Add(stop);

            }
        }
        var declaration = (Animation: animation, Target: target, Channel: channel, Layer: layer, Count: 0, TrackCount: 0);
        {
            using var tracks = world.ReadBuffer<Track<T>>(animation);
            declaration.TrackCount = tracks.Count;
            for (int index = 0; index < tracks.Count; index++)
            {
                Track<T> track = tracks[index];
                if (track.Typed && !track.FromGraph && track.Channel == declaration.Channel && track.Layer == declaration.Layer &&
                    (track.Target == Entity.Null ? declaration.Animation : track.Target) == declaration.Target)
                    declaration.Count++;
            }
        }
        bool addedSources = declaration.Count < capacity;
        while (declaration.Count++ < capacity)
        {
            int index = declaration.TrackCount++;
            var source = new PropertySource<T> { Animation = animation, Channel = channel, Layer = layer, Track = index };
            var boundary = new DirectBoundary { ValueType = ComponentId<Buffer<Track<T>>>.Value, Track = index };
            {
                using var boundaries = world.GetBuffer<DirectBoundary>(animation);
                Visit(boundaries, ref boundary);

                static void Visit(DynamicBuffer<DirectBoundary> boundaries, ref DirectBoundary boundary)
                {
                    for (int index = 0; index < boundaries.Count; index++)
                        if (boundaries[index].ValueType == boundary.ValueType && boundaries[index].Track == boundary.Track)
                        { boundaries[index] = boundary; return; }
                    boundaries.Add(boundary);

                }
            }
            var track = new Track<T>
            {
                Target = animation == target ? Entity.Null : target,
                Channel = channel,
                Layer = layer,
                Typed = true,
                Finished = true
            };
            {
                using var tracks = world.GetBuffer<Track<T>>(animation);
                tracks.Add(track);
            }
            {
                using var sources = world.GetBuffer<PropertySource<T>>(target);
                sources.Add(source);
            }
        }
        _ = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        if (addedSources) RefreshSources<T>(world, target);
    }

    public static Entity To<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target,
        Channel<T> channel, T value, Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer = 0)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> =>
        StartTyped(world, target, channel, value, spec, layer, TrackEndpoints.Values);

    public static Entity By<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target,
        Channel<T> channel, T delta, Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer = 0)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> =>
        StartTyped(world, target, channel, delta, spec, layer, TrackEndpoints.Displacement);

    private static unsafe Entity StartTyped<T, TOps, TParameters, TState, TEvaluator>(World world, Entity target,
        Channel<T> channel, T value, Spec<T, TOps, TParameters, TState, TEvaluator> spec, byte layer, TrackEndpoints endpoints)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        PropertySource<T> source = FindSource(world, target, channel, layer, endpoints);
        if (source.Animation != Entity.Null && source.Animation != target && endpoints == TrackEndpoints.Values &&
            world.Has<Timeline>(target) && world.Has<Buffer<Track<T>>>(target))
        {
            DeclareTyped(world, target, target, channel, spec, 1, layer);
            MoveTypedSource<T, TOps, TParameters, TState, TEvaluator>(world, target, source);
            source = FindSource(world, target, channel, layer, endpoints);
        }
        if (source.Animation == Entity.Null)
        {
            Entity animation = world.Has<Timeline>(target) && world.Has<Buffer<Track<T>>>(target) ? target : FindTypedPool<T, TOps, TParameters, TState, TEvaluator>(world);
            DeclareTyped(world, animation, target, channel, spec, 1, layer);
            source = FindSource(world, target, channel, layer, endpoints);
        }
        Entity instance = source.Animation;
        DevelopmentCheck.Require(world.Has<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>(instance),
            "The selected algorithm must be declared on the animation instance.");
        Track<T> previous = ReadTrack<T>(world, instance, source.Track);
        Timeline timeline = ReadStartingTimeline(world, instance);
        T current = source.Active ? source.Value : previous.SourceActive && (!previous.Finished || HoldsLayerValue(world, instance, in previous))
            ? previous.Current : channel.Read(world, target);
        bool presentationChanged = false;
        if (previous.SourceActive && source.Direct)
        {
            T presented = channel.Read(world, target);
            presentationChanged = !TOps.Equals(in presented, in previous.Current);
            if (presentationChanged) current = presented;
        }
        if (world.Has<AnimationLayers>(target) && (endpoints == TrackEndpoints.Displacement || !previous.SourceActive &&
            world.ReadComponentData<AnimationLayers>(target).Items[layer].Mode == LayerMode.Additive)) ReadBase(world, target, channel, ref current);
        T from = endpoints == TrackEndpoints.Displacement ? TOps.Add(in current, in value) : current;
        T to = endpoints == TrackEndpoints.Displacement ? current : value;
        var values = new MotionValues<T>(from, to, from);
        int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        bool same = previous.MotionType == type;
        bool running = same && previous.MotionInitialized;
        if (!same && previous.MotionInitialized)
            MotionAccesses<T>.Items[previous.MotionType - 1].Release(world, instance, previous.MotionIndex);
        int stateIndex = same ? previous.MotionIndex : FreeMotionIndex<T, TOps, TParameters, TState, TEvaluator>(world, instance);
        MotionStorage<T, TOps, TParameters, TState, TEvaluator>.UseInstanceParameters(world, instance);
        var change = (Parameters: spec.Parameters, Previous: previous, Values: values, Running: running, Index: stateIndex,
            Unchanged: false, ParametersOnly: false, Endpoints: endpoints, Target: value, PresentationChanged: presentationChanged);
        {
            using var instances = world.GetBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(instance);
            Visit(instances, ref change);

            static void Visit(DynamicBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>> instances, ref (TParameters Parameters, Track<T> Previous, MotionValues<T> Values, bool Running, int Index, bool Unchanged, bool ParametersOnly, TrackEndpoints Endpoints, T Target, bool PresentationChanged) change)
            {
                ref MotionData<T, TParameters, TState> data = ref instances[change.Index].Data;
                bool equal = change.Running && TEvaluator.ParametersEqual(in data.Parameters, in change.Parameters);
                bool sameTarget = !change.PresentationChanged && change.Previous.Endpoints == TrackEndpoints.Values && change.Endpoints == TrackEndpoints.Values &&
                    TOps.Equals(in change.Previous.To, in change.Target);
                if (change.Previous.SourceActive && sameTarget && equal &&
                    (change.Running || TOps.Equals(in change.Previous.Current, in change.Target)) &&
                    change.Previous.MotionType == MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type)
                {
                    change.Unchanged = true;
                    return;
                }
                if (change.Running)
                {
                    if (!equal) TEvaluator.UpdateParameters(in data.Parameters, in change.Parameters, ref data.State);
                    if (sameTarget)
                    {
                        change.ParametersOnly = true;
                        change.Values = new(change.Previous.From, change.Previous.To, change.Previous.Current);
                    }
                    else TEvaluator.Retarget(in data.Parameters, in change.Parameters, in change.Values, ref data.State);
                }
                else TEvaluator.Initialize(in change.Parameters, in change.Values, ref data.State);
                data.Parameters = change.Parameters;
                data.Values = change.Values;
                data.Active = true;

            }
        }
        if (change.Unchanged) return instance;
        if (change.ParametersOnly)
        {
            previous.Duration = spec.Boundary.Duration;
            previous.End = spec.Boundary.End;
            WriteTrack(world, instance, source.Track, in previous);
            if (world.IsEnabled<Timeline>(instance)) UpdateDirectBoundary(world, instance, source.Track, in previous, in timeline, spec.Boundary);
            return instance;
        }
        var track = new Track<T>
        {
            Target = target == instance ? Entity.Null : target,
            Channel = channel,
            Layer = layer,
            Typed = true,
            SourceActive = true,
            MotionType = type,
            MotionIndex = change.Index,
            MotionInitialized = true,
            SourceIndex = previous.SourceIndex,
            From = from,
            To = to,
            Current = from,
            Start = timeline.Time,
            SampleTime = timeline.Time,
            StartId = timeline.Starts,
            Stage = timeline.Stage,
            Duration = spec.Boundary.Duration,
            End = spec.Boundary.End,
            Endpoints = endpoints == TrackEndpoints.Displacement ? TrackEndpoints.DisplacementValues : endpoints,
            Displacement = endpoints == TrackEndpoints.Displacement ? value : default,
        };
        WriteTrack(world, instance, source.Track, in track);
        world.Set(instance, timeline);
        UpdateDirectBoundary(world, instance, source.Track, in track, in timeline, spec.Boundary);
        world.Enable<Timeline>(instance);
        return instance;
    }

    private static PropertySource<T> FindSource<T>(World world, Entity target, Channel<T> channel, byte layer, TrackEndpoints endpoints) where T : unmanaged
    {
        if (!world.Has<Buffer<PropertySource<T>>>(target)) return default;
        var search = (World: world, Target: target, Channel: channel, Layer: layer, Endpoints: endpoints, Result: default(PropertySource<T>));
        {
            using QueryScope read = SourceQueries<T>.For(world).Open();
            Visit(in read, ref search);

            static void Visit(scoped in QueryScope rows, ref (World World, Entity Target, Channel<T> Channel, byte Layer, TrackEndpoints Endpoints, PropertySource<T> Result) search)
            {
                foreach (QueryChunk chunk in rows)
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        if (chunk.Entities[rowIndex] != search.Target) continue;
                        ReadOnlyBuffer<PropertySource<T>> sources = chunk.ReadBuffer<PropertySource<T>>(rowIndex);
                        foreach (PropertySource<T> source in sources.AsSpan())
                        {
                            if (source.Channel != search.Channel || source.Layer != search.Layer || !search.World.IsAlive(source.Animation)) continue;
                            Track<T> track = ReadTrack<T>(search.World, source.Animation, source.Track);
                            if (track.FromGraph) continue;
                            if ((track.Target == Entity.Null ? source.Animation : track.Target) != search.Target) continue;
                            if (search.Endpoints == TrackEndpoints.Values && track.SourceActive && track.Endpoints == TrackEndpoints.Values)
                            { search.Result = source; return; }
                            if (!track.SourceActive || track.Finished && !HoldsLayerValue(search.World, source.Animation, in track)) search.Result = source;
                        }
                    }
            }
        }
        return search.Result;
    }

    private static int FreeMotionIndex<T, TOps, TParameters, TState, TEvaluator>(World world, Entity animation)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        int capacity = 0;
        {
            using var instances = world.ReadBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
            capacity = instances.Count;
        }
        var search = (Capacity: capacity, Type: MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type, Index: -1);
        {
            using var tracks = world.ReadBuffer<Track<T>>(animation);
            Visit(tracks, ref search);

            static void Visit(ReadOnlyBuffer<Track<T>> tracks, ref (int Capacity, int Type, int Index) search)
            {
                for (int candidate = 0; candidate < search.Capacity; candidate++)
                {
                    bool used = false;
                    foreach (Track<T> track in tracks.AsSpan())
                        if (track.SourceActive && track.MotionType == search.Type && track.MotionIndex == candidate) { used = true; break; }
                    if (!used) { search.Index = candidate; return; }
                }

            }
        }
        DevelopmentCheck.Require(search.Index >= 0, "Playback requires declared algorithm capacity.");
        return search.Index;
    }

    private static unsafe Entity FindTypedPool<T, TOps, TParameters, TState, TEvaluator>(World world)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var query = world.Query(new QueryDescription().WithStored<Timeline>().WithStored<PooledAnimation>()
            .WithStored<Buffer<Track<T>>>().WithStored<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>().WithDisabled<Timeline>());
        var available = (World: world, Animation: Entity.Null);
        {
            using QueryScope read = query.Open();
            Visit(in read, ref available);

            static void Visit(scoped in QueryScope rows, ref (World World, Entity Animation) available)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<Track<T>>>() ? chunk.GetBuffers<Track<T>>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        bool holds = false;
                        DynamicBuffer<Track<T>> tracks = buffers0[rowIndex];
                        for (int index = 0; index < tracks.Count; index++)
                        {
                            Track<T> track = tracks[index];
                            holds |= HoldsLayerValue(available.World, chunk.Entities[rowIndex], in track);
                        }
                        if (holds) continue;
                        available.Animation = chunk.Entities[rowIndex];
                        return;

                    }
                }

            }
        }

        if (available.Animation != Entity.Null)
        {
            int count = BufferCount<Track<T>>(world, available.Animation);
            for (int index = 0; index < count; index++)
            {
                Track<T> track = ReadTrack<T>(world, available.Animation, index);
                if (track.MotionInitialized) MotionAccesses<T>.Items[track.MotionType - 1].Release(world, available.Animation, track.MotionIndex);
                Entity target = track.Target == Entity.Null ? available.Animation : track.Target;
                if (!world.IsAlive(target) || !world.Has<Buffer<PropertySource<T>>>(target)) continue;
                Entity previous = available.Animation;
                {
                    using var sources = world.GetBuffer<PropertySource<T>>(target);
                    for (int source = sources.Count - 1; source >= 0; source--)
                        if (sources[source].Animation == previous) sources.RemoveAtSwapBack(source);
                }
                RefreshSources<T>(world, target);
            }
            {
                using var tracks = world.GetBuffer<Track<T>>(available.Animation);
                tracks.Clear();
            }
            Timeline previousTimeline = world.ReadComponentData<Timeline>(available.Animation);
            world.Set(available.Animation, new Timeline { Starts = previousTimeline.Starts, Completions = previousTimeline.Completions });
            if (world.Has<Buffer<TimelineStage>>(available.Animation))
            {
                using var stages = world.GetBuffer<TimelineStage>(available.Animation);
                Visit(stages);

                static void Visit(DynamicBuffer<TimelineStage> stages)
                {
                    while (stages.Count > 0) stages.RemoveAtSwapBack(stages.Count - 1);

                }
            }
            return available.Animation;
        }
        Entity animation = world.Create(new Timeline());
        world.Add(animation, default(PooledAnimation));
        world.Disable<Timeline>(animation);
        return animation;
    }

    internal static Track<T> ReadTrack<T>(World world, Entity animation, int index) where T : unmanaged
    {
        var item = (Index: index, Track: default(Track<T>));
        {
            using var tracks = world.ReadBuffer<Track<T>>(animation);
            item.Track = tracks[item.Index];
        }
        return item.Track;
    }

    internal static void WriteTrack<T>(World world, Entity animation, int index, in Track<T> track) where T : unmanaged
    {
        var item = (Index: index, Track: track);
        {
            using var tracks = world.GetBuffer<Track<T>>(animation);
            tracks[item.Index] = item.Track;
        }
    }

    internal static void SubmitTyped<T, TOps>(World world, Entity animation, uint output, in Track<T> track)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        if (TrySubmitTyped<T, TOps>(world, animation, output, in track, out FieldWrite<T> write))
            write.Channel.Write(world, write.Target, in write.Value);
    }

    internal static bool TrySubmitTyped<T, TOps>(World world, Entity animation, uint output, in Track<T> track, out FieldWrite<T> write)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        write = default;
        Entity target = track.Target == Entity.Null ? animation : track.Target;
        if (track.Typed && world.Has<Buffer<PropertySource<T>>>(target))
        {
            var outputValue = (Index: track.SourceIndex, Track: track, Direct: !world.Has<Buffer<Contribution<T>>>(target));
            {
                using var sources = world.GetBuffer<PropertySource<T>>(target);
                outputValue.Direct &= UpdateSource<T, TOps>(sources, outputValue.Index, in outputValue.Track);
            }
            if (!outputValue.Direct) return false;
        }
        T value = track.Current;
        if (track.Endpoints != TrackEndpoints.Values && world.Has<AnimationLayers>(target) &&
            world.ReadComponentData<AnimationLayers>(target).Items[track.Layer].Mode == LayerMode.Additive)
        {
            T basis = track.To;
            ReadBase(world, target, track.Channel, ref basis);
            T displacement = TOps.Subtract(in value, in track.To);
            value = TOps.Add(in basis, in displacement);
        }
        if (world.Has<Buffer<Contribution<T>>>(target))
        {
            var contribution = new Contribution<T> { Channel = track.Channel, Layer = track.Layer, Value = value, Source = animation, Output = output };
            {
                using var contributions = world.GetBuffer<Contribution<T>>(target);
                contributions.Add(contribution);
            }
        }
        else if (TrySingleValue<T, TOps>(world, target, track.Channel, track.Layer, ref value))
        {
            write = new(target, track.Channel, value);
            return true;
        }
        return false;
    }

    internal static bool UpdateSource<T, TOps>(DynamicBuffer<PropertySource<T>> sources, int index, in Track<T> track)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        PropertySource<T> previous = sources[index];
        if (!previous.Active || !TOps.Equals(in previous.Value, in track.Current) ||
            !TOps.Equals(in previous.Reference, in track.To) || previous.Endpoints != track.Endpoints)
        {
            previous.Value = track.Current;
            previous.Reference = track.To;
            previous.Endpoints = track.Endpoints;
            previous.Active = true;
            previous.Dirty = true;
            sources[index] = previous;
        }
        return previous.Direct;
    }
}
