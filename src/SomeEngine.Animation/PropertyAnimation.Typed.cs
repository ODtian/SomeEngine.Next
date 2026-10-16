using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

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
        if (!world.HasBuffer<PropertySource<T>>(target)) world.AddBuffer<PropertySource<T>>(target);
        if (!world.HasBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation))
            world.AddBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
        if (!world.HasBuffer<MotionDeclaration<T>>(animation)) world.AddBuffer<MotionDeclaration<T>>(animation);
        if (!world.HasBuffer<MotionStop>(animation)) world.AddBuffer<MotionStop>(animation);
        if (!world.HasBuffer<DirectBoundary>(animation)) world.AddBuffer<DirectBoundary>(animation);
        if (!world.HasBuffer<TimelineStage>(animation)) world.AddBuffer<TimelineStage>(animation);
        var reservation = (Declaration: new MotionDeclaration<T> { Channel = channel, Layer = layer, Capacity = capacity,
            Type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type }, Additional: capacity);
        world.ExecuteBufferWrite<MotionDeclaration<T>, (MotionDeclaration<T> Declaration, int Additional)>(animation, ref reservation,
            static (declarations, ref reservation) =>
            {
                for (int index = 0; index < declarations.Count; index++)
                {
                    MotionDeclaration<T> previous = declarations.Read(index);
                    if (previous.Channel != reservation.Declaration.Channel || previous.Layer != reservation.Declaration.Layer || previous.Type != reservation.Declaration.Type) continue;
                    reservation.Additional = Math.Max(0, reservation.Declaration.Capacity - previous.Capacity);
                    if (reservation.Additional > 0) declarations[index] = reservation.Declaration;
                    return;
                }
                declarations.Add(reservation.Declaration);
            });
        int additional = reservation.Additional;
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, int>(animation, ref additional,
            static (instances, ref additional) =>
            {
                int count = instances.Count + additional;
                instances.EnsureCapacity(count);
                while (instances.Count < count) instances.Add(default);
            });
        var stop = new MotionStop { Type = reservation.Declaration.Type, ValueType = BufferComponents.Header<Track<T>>(),
            Stop = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.StopFunction };
        world.ExecuteBufferWrite<MotionStop, MotionStop>(animation, ref stop, static (stops, ref stop) =>
        {
            foreach (MotionStop previous in stops.AsSpan()) if (previous.Type == stop.Type && previous.ValueType == stop.ValueType) return;
            stops.Add(stop);
        });
        var declaration = (Animation: animation, Target: target, Channel: channel, Layer: layer, Count: 0, TrackCount: 0);
        world.ExecuteBufferRead<Track<T>, (Entity Animation, Entity Target, Channel<T> Channel, byte Layer, int Count, int TrackCount)>(animation, ref declaration,
            static (tracks, ref declaration) =>
            {
                declaration.TrackCount = tracks.Count;
                for (int index = 0; index < tracks.Count; index++)
                {
                    Track<T> track = tracks[index];
                    if (track.Typed && track.Channel == declaration.Channel && track.Layer == declaration.Layer &&
                        (track.Target == Entity.Null ? declaration.Animation : track.Target) == declaration.Target)
                        declaration.Count++;
                }
            });
        while (declaration.Count++ < capacity)
        {
            int index = declaration.TrackCount++;
            var source = new PropertySource<T> { Animation = animation, Channel = channel, Layer = layer, Track = index };
            var boundary = new DirectBoundary { ValueType = BufferComponents.Header<Track<T>>(), Track = index };
            world.ExecuteBufferWrite<DirectBoundary, DirectBoundary>(animation, ref boundary,
                static (boundaries, ref boundary) =>
                {
                    for (int index = 0; index < boundaries.Count; index++)
                        if (boundaries.Read(index).ValueType == boundary.ValueType && boundaries.Read(index).Track == boundary.Track)
                        { boundaries[index] = boundary; return; }
                    boundaries.Add(boundary);
                });
            var track = new Track<T> { Target = animation == target ? Entity.Null : target,
                Channel = channel, Layer = layer, Typed = true, Finished = true };
            world.ExecuteBufferWrite<Track<T>, Track<T>>(animation, ref track, static (tracks, ref track) => tracks.Add(track));
            world.ExecuteBufferWrite<PropertySource<T>, PropertySource<T>>(target, ref source, static (sources, ref source) => sources.Add(source));
        }
        _ = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        RefreshSources<T>(world, target);
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
        if (source.Animation == Entity.Null)
        {
            Entity animation = world.Has<Timeline>(target) && world.HasBuffer<Track<T>>(target) ? target : FindTypedPool<T, TOps, TParameters, TState, TEvaluator>(world);
            DeclareTyped(world, animation, target, channel, spec, 1, layer);
            source = FindSource(world, target, channel, layer, endpoints);
        }
        Entity instance = source.Animation;
        DevelopmentCheck.Require(world.HasBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(instance),
            "The selected algorithm must be declared on the animation instance.");
        Track<T> previous = ReadTrack<T>(world, instance, source.Track);
        Timeline timeline = ReadStartingTimeline(world, instance);
        T current = previous.SourceActive ? previous.Current : channel.Read(world, target);
        if (world.Has<AnimationLayers>(target) && (endpoints == TrackEndpoints.Displacement || !previous.SourceActive &&
            world.Read<AnimationLayers>(target).Items[layer].Mode == LayerMode.Additive)) ReadBase(world, target, channel, ref current);
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
            Unchanged: false, ParametersOnly: false, Endpoints: endpoints, Target: value);
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>,
            (TParameters Parameters, Track<T> Previous, MotionValues<T> Values, bool Running, int Index, bool Unchanged, bool ParametersOnly, TrackEndpoints Endpoints, T Target)>(instance, ref change,
            static (instances, ref change) =>
            {
                ref MotionData<T, TParameters, TState> data = ref instances[change.Index].Data;
                bool equal = TEvaluator.ParametersEqual(in data.Parameters, in change.Parameters);
                bool sameTarget = change.Previous.Endpoints == TrackEndpoints.Values && change.Endpoints == TrackEndpoints.Values &&
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
            });
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
            Target = target == instance ? Entity.Null : target, Channel = channel, Layer = layer,
            Typed = true, SourceActive = true, MotionType = type, MotionIndex = change.Index, MotionInitialized = true, SourceIndex = previous.SourceIndex,
            From = from, To = to, Current = from, Start = timeline.Time, SampleTime = timeline.Time,
            StartId = timeline.Starts, Stage = timeline.Stage, Duration = spec.Boundary.Duration,
            End = spec.Boundary.End,
            Endpoints = endpoints == TrackEndpoints.Displacement ? TrackEndpoints.DisplacementValues : endpoints,
            Displacement = endpoints == TrackEndpoints.Displacement ? value : default,
        };
        WriteTrack(world, instance, source.Track, in track);
        world.Replace(instance, timeline);
        UpdateDirectBoundary(world, instance, source.Track, in track, in timeline, spec.Boundary);
        world.Enable<Timeline>(instance);
        return instance;
    }

    private static PropertySource<T> FindSource<T>(World world, Entity target, Channel<T> channel, byte layer, TrackEndpoints endpoints) where T : unmanaged
    {
        if (!world.HasBuffer<PropertySource<T>>(target)) return default;
        var search = (World: world, Target: target, Channel: channel, Layer: layer, Endpoints: endpoints, Result: default(PropertySource<T>));
        world.ExecuteQuery(SourceQueries<T>.For(world), ref search,
            static (QueryCursor rows, ref (World World, Entity Target, Channel<T> Channel, byte Layer, TrackEndpoints Endpoints, PropertySource<T> Result) search) =>
            {
                if (!rows.TryGetRow(search.Target, out QueryRow row)) return;
                BufferView<PropertySource<T>> sources = row.ReadBuffer<PropertySource<T>>();
                foreach (PropertySource<T> source in sources.AsSpan())
                {
                    if (source.Channel != search.Channel || source.Layer != search.Layer || !search.World.IsAlive(source.Animation)) continue;
                    Track<T> track = ReadTrack<T>(search.World, source.Animation, source.Track);
                    if ((track.Target == Entity.Null ? source.Animation : track.Target) != search.Target) continue;
                    if (search.Endpoints == TrackEndpoints.Values && track.SourceActive && track.Endpoints == TrackEndpoints.Values)
                    { search.Result = source; return; }
                    if (!track.SourceActive || track.Finished && !HoldsLayerValue(search.World, source.Animation, in track)) search.Result = source;
                }
            });
        return search.Result;
    }

    private static int FreeMotionIndex<T, TOps, TParameters, TState, TEvaluator>(World world, Entity animation)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        int capacity = 0;
        world.ExecuteBufferRead<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, int>(animation, ref capacity,
            static (instances, ref capacity) => capacity = instances.Count);
        var search = (Capacity: capacity, Type: MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type, Index: -1);
        world.ExecuteBufferRead<Track<T>, (int Capacity, int Type, int Index)>(animation, ref search,
            static (tracks, ref search) =>
            {
                for (int candidate = 0; candidate < search.Capacity; candidate++)
                {
                    bool used = false;
                    foreach (Track<T> track in tracks.AsSpan())
                        if (track.SourceActive && track.MotionType == search.Type && track.MotionIndex == candidate) { used = true; break; }
                    if (!used) { search.Index = candidate; return; }
                }
            });
        DevelopmentCheck.Require(search.Index >= 0, "Playback requires declared algorithm capacity.");
        return search.Index;
    }

    private static unsafe Entity FindTypedPool<T, TOps, TParameters, TState, TEvaluator>(World world)
        where T : unmanaged where TOps : struct, IValueOps<T> where TParameters : struct where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        var query = world.Query(world.QueryDefinition().Read<Timeline>().All<PooledAnimation>()
            .WriteBuffer<Track<T>>().WriteBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>().Disabled<Timeline>());
        var available = (World: world, Animation: Entity.Null);
        world.ExecuteQuery(query, ref available, static (QueryCursor rows, ref (World World, Entity Animation) available) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                bool holds = false;
                DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                for (int index = 0; index < tracks.Count; index++)
                {
                    Track<T> track = tracks.Read(index);
                    holds |= HoldsLayerValue(available.World, row.Entity, in track);
                }
                if (holds) continue;
                available.Animation = row.Entity;
                return;
            }
        });
        world.ReleaseQuery(query);
        if (available.Animation != Entity.Null)
        {
            int count = BufferCount<Track<T>>(world, available.Animation);
            for (int index = 0; index < count; index++)
            {
                Track<T> track = ReadTrack<T>(world, available.Animation, index);
                if (track.MotionInitialized) MotionAccesses<T>.Items[track.MotionType - 1].Release(world, available.Animation, track.MotionIndex);
                Entity target = track.Target == Entity.Null ? available.Animation : track.Target;
                if (!world.IsAlive(target) || !world.HasBuffer<PropertySource<T>>(target)) continue;
                Entity previous = available.Animation;
                world.ExecuteBufferWrite<PropertySource<T>, Entity>(target, ref previous,
                    static (sources, ref previous) =>
                    {
                        for (int source = sources.Count - 1; source >= 0; source--)
                            if (sources.Read(source).Animation == previous) sources.SwapRemoveAt(source);
                    });
                RefreshSources<T>(world, target);
            }
            world.ExecuteBufferWrite<Track<T>>(available.Animation, static tracks => tracks.Clear());
            return available.Animation;
        }
        Entity animation = world.CreateEntity(new Timeline());
        world.AddTag<PooledAnimation>(animation);
        world.Disable<Timeline>(animation);
        return animation;
    }

    internal static Track<T> ReadTrack<T>(World world, Entity animation, int index) where T : unmanaged
    {
        var item = (Index: index, Track: default(Track<T>));
        world.ExecuteBufferRead<Track<T>, (int Index, Track<T> Track)>(animation, ref item,
            static (tracks, ref item) => item.Track = tracks[item.Index]);
        return item.Track;
    }

    internal static void WriteTrack<T>(World world, Entity animation, int index, in Track<T> track) where T : unmanaged
    {
        var item = (Index: index, Track: track);
        world.ExecuteBufferWrite<Track<T>, (int Index, Track<T> Track)>(animation, ref item,
            static (tracks, ref item) => tracks[item.Index] = item.Track);
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
        if (track.Typed && world.HasBuffer<PropertySource<T>>(target))
        {
            var outputValue = (Index: track.SourceIndex, Track: track, Direct: !world.HasBuffer<Contribution<T>>(target));
            world.ExecuteBufferWrite<PropertySource<T>, (int Index, Track<T> Track, bool Direct)>(target, ref outputValue,
                static (sources, ref outputValue) =>
                {
                    PropertySource<T> previous = sources.Read(outputValue.Index);
                    outputValue.Direct &= previous.Direct;
                    if (!previous.Active || !TOps.Equals(in previous.Value, in outputValue.Track.Current) ||
                        !TOps.Equals(in previous.Reference, in outputValue.Track.To) || previous.Endpoints != outputValue.Track.Endpoints)
                    {
                        previous.Value = outputValue.Track.Current;
                        previous.Reference = outputValue.Track.To;
                        previous.Endpoints = outputValue.Track.Endpoints;
                        previous.Active = true;
                        previous.Dirty = true;
                        sources[outputValue.Index] = previous;
                    }
                });
            if (!outputValue.Direct) return false;
        }
        T value = track.Current;
        if (track.Endpoints != TrackEndpoints.Values && world.Has<AnimationLayers>(target) &&
            world.Read<AnimationLayers>(target).Items[track.Layer].Mode == LayerMode.Additive)
        {
            T basis = track.To;
            ReadBase(world, target, track.Channel, ref basis);
            T displacement = TOps.Subtract(in value, in track.To);
            value = TOps.Add(in basis, in displacement);
        }
        if (world.HasBuffer<Contribution<T>>(target))
        {
            var contribution = new Contribution<T> { Channel = track.Channel, Layer = track.Layer, Value = value, Source = animation, Output = output };
            world.ExecuteBufferWrite<Contribution<T>, Contribution<T>>(target, ref contribution,
                static (contributions, ref contribution) => contributions.Add(contribution));
        }
        else if (TrySingleValue<T, TOps>(world, target, track.Channel, track.Layer, ref value))
        {
            write = new(target, track.Channel, value);
            return true;
        }
        return false;
    }
}
