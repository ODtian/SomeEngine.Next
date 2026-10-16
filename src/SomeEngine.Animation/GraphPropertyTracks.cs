using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

internal struct GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator> : IComponent
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    internal TParameters[]? Values;
}

[BufferCapacity(1)]
internal struct GraphFieldOutput<T> : IBufferElement where T : unmanaged
{
    internal int Id, Leader, First, Count;
}

[BufferCapacity(4)]
internal struct GraphFieldTrack<T> : IBufferElement where T : unmanaged
{
    internal int Track;
}

internal static class GraphPropertyTracks<T, TOps, TParameters, TState, TEvaluator>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    internal static unsafe void Install(World world, Entity animation, Entity target,
        in PropertyLeafParameters<T, TOps, TParameters, TState, TEvaluator> definition,
        TParameters[] parameters, int index, in AnimationLeafPlacement placement)
    {
        PropertyAnimation.Declare<T>(world, animation);
        if (!world.HasBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation))
            world.AddBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
        var shared = new GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator> { Values = parameters };
        if (world.Has<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(animation))
            world.Replace(animation, shared);
        else
            world.Add(animation, shared);
        int count = parameters.Length;
        world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>, int>(animation, ref count,
            static (instances, ref count) =>
            {
                instances.EnsureCapacity(count);
                while (instances.Count < count)
                    instances.Add(default);
            });
        if (!world.HasBuffer<MotionStop>(animation))
            world.AddBuffer<MotionStop>(animation);
        var stop = new MotionStop
        {
            Type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type,
            ValueType = BufferComponents.Header<Track<T>>(),
            Stop = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.StopFunction,
        };
        world.ExecuteBufferWrite<MotionStop, MotionStop>(animation, ref stop, static (stops, ref stop) =>
        {
            foreach (MotionStop previous in stops.AsSpan())
                if (previous.Type == stop.Type)
                    return;
            stops.Add(stop);
        });
        var track = new Track<T>
        {
            Target = target == animation ? Entity.Null : target,
            Channel = definition.Channel, To = definition.Value, Duration = definition.Spec.Boundary.Duration,
            Layer = placement.Layer, Stage = placement.Stage, Start = placement.Start, SampleTime = placement.Start,
            StartId = placement.Starts, Endpoints = definition.Endpoints, CaptureStart = true,
            Typed = true, SourceActive = true,
            MotionType = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type, MotionIndex = index,
            GraphMotion = placement.Motion, GraphGroup = placement.Group, MotionStage = placement.MotionStage,
            GraphOutput = placement.Output, FromGraph = true,
        };
        world.ExecuteBufferWrite<Track<T>, Track<T>>(animation, ref track, static (tracks, ref track) =>
        {
            if (tracks.Count > 0 && tracks.Read(0).StartId != track.StartId)
                tracks.Clear();
            tracks.Add(track);
        });
    }

    internal static void Complete(World world, Entity animation) => GraphFieldOutputs<T>.Build(world, animation);
}

internal static class GraphFieldOutputs<T> where T : unmanaged
{
    internal static void Build(World world, Entity animation)
    {
        if (!world.HasBuffer<GraphFieldOutput<T>>(animation))
            world.AddBuffer<GraphFieldOutput<T>>(animation);
        if (!world.HasBuffer<GraphFieldTrack<T>>(animation))
            world.AddBuffer<GraphFieldTrack<T>>(animation);
        var values = new List<Track<T>>();
        world.ExecuteBufferRead<Track<T>, List<Track<T>>>(animation, ref values, static (tracks, ref values) =>
        {
            foreach (Track<T> track in tracks.AsSpan())
                values.Add(track);
        });
        var outputs = new SortedDictionary<int, List<int>>();
        var targets = new HashSet<Entity>();
        for (int index = 0; index < values.Count; index++)
        {
            Track<T> track = values[index];
            if (!track.FromGraph)
                continue;
            if (!outputs.TryGetValue(track.GraphOutput, out List<int>? tracks))
                outputs.Add(track.GraphOutput, tracks = new List<int>());
            tracks.Add(index);
            targets.Add(track.Target == Entity.Null ? animation : track.Target);
        }
        foreach (Entity target in targets)
        {
            if (!world.HasBuffer<PropertySource<T>>(target))
                world.AddBuffer<PropertySource<T>>(target);
            Entity source = animation;
            world.ExecuteBufferWrite<PropertySource<T>, Entity>(target, ref source, static (sources, ref source) =>
            {
                for (int index = sources.Count - 1; index >= 0; index--)
                    if (sources.Read(index).Animation == source)
                        sources.SwapRemoveAt(index);
            });
        }
        world.ExecuteBufferWrite<GraphFieldOutput<T>>(animation, static outputs => outputs.Clear());
        world.ExecuteBufferWrite<GraphFieldTrack<T>>(animation, static tracks => tracks.Clear());
        foreach ((int id, List<int> tracks) in outputs)
        {
            tracks.Sort((first, second) =>
            {
                Track<T> a = values[first], b = values[second];
                int order = a.GraphMotion.CompareTo(b.GraphMotion);
                if (order == 0) order = (a.GraphMotion == 0 ? a.Stage : a.MotionStage).CompareTo(b.GraphMotion == 0 ? b.Stage : b.MotionStage);
                if (order == 0) order = a.Start.CompareTo(b.Start);
                return order == 0 ? first.CompareTo(second) : order;
            });
            int first = 0;
            world.ExecuteBufferWrite<GraphFieldTrack<T>, int>(animation, ref first, static (buffer, ref first) => first = buffer.Count);
            foreach (int track in tracks)
            {
                var value = new GraphFieldTrack<T> { Track = track };
                world.ExecuteBufferWrite<GraphFieldTrack<T>, GraphFieldTrack<T>>(animation, ref value,
                    static (buffer, ref value) => buffer.Add(value));
            }
            var output = new GraphFieldOutput<T> { Id = id, Leader = tracks[0], First = first, Count = tracks.Count };
            world.ExecuteBufferWrite<GraphFieldOutput<T>, GraphFieldOutput<T>>(animation, ref output,
                static (buffer, ref output) => buffer.Add(output));
            Track<T> leader = values[tracks[0]];
            Entity target = leader.Target == Entity.Null ? animation : leader.Target;
            var source = new PropertySource<T> { Animation = animation, Channel = leader.Channel, Layer = leader.Layer, Track = tracks[0] };
            world.ExecuteBufferWrite<PropertySource<T>, PropertySource<T>>(target, ref source,
                static (sources, ref source) => sources.Add(source));
        }
        foreach (Entity target in targets)
            PropertyAnimation.RefreshSources<T>(world, target);
        foreach ((int _, List<int> indices) in outputs)
        {
            var update = (Indices: indices, Source: 0);
            world.ExecuteBufferWrite<Track<T>, (List<int> Indices, int Source)>(animation, ref update,
                static (tracks, ref update) =>
                {
                    update.Source = tracks.Read(update.Indices[0]).SourceIndex;
                    foreach (int index in update.Indices)
                        tracks[index].SourceIndex = update.Source;
                });
        }
    }
}
