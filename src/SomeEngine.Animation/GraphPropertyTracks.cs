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

internal struct GraphFieldOutput<T> : IBufferElement where T : unmanaged
{
    internal int Id, Leader, First, Count;
}

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
        PropertyAnimation.DeclareStorage<T>(world, animation);
        if (!world.Has<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>(animation))
            world.AddBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
        var shared = new GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator> { Values = parameters };
        if (world.Has<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(animation))
            world.Set(animation, shared);
        else
            world.Add(animation, shared);
        int count = parameters.Length;
        {
            using var instances = world.GetBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>(animation);
            instances.EnsureCapacity(count);
            while (instances.Count < count)
                instances.Add(default);
        }
        if (!world.Has<Buffer<MotionStop>>(animation))
            world.AddBuffer<MotionStop>(animation);
        var stop = new MotionStop
        {
            Type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type,
            ValueType = ComponentId<Buffer<Track<T>>>.Value,
            Stop = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.StopFunction,
        };
        {
            using var stops = world.GetBuffer<MotionStop>(animation);
            Visit(stops, ref stop);

            static void Visit(DynamicBuffer<MotionStop> stops, ref MotionStop stop)
            {
                foreach (MotionStop previous in stops.AsSpan())
                    if (previous.Type == stop.Type)
                        return;
                stops.Add(stop);

            }
        }
        var track = new Track<T>
        {
            Target = target == animation ? Entity.Null : target,
            Channel = definition.Channel,
            To = definition.Value,
            Duration = definition.Spec.Boundary.Duration,
            End = definition.Spec.Boundary.End,
            Layer = placement.Layer,
            Stage = placement.Stage,
            Start = placement.Start,
            SampleTime = placement.Start,
            StartId = placement.Starts,
            Endpoints = definition.Endpoints,
            CaptureStart = true,
            Typed = true,
            SourceActive = true,
            MotionType = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type,
            MotionIndex = index,
            GraphMotion = placement.Motion,
            GraphGroup = placement.Group,
            MotionStage = placement.MotionStage,
            GraphOutput = placement.Output,
            FromGraph = true,
        };
        {
            using var tracks = world.GetBuffer<Track<T>>(animation);
            if (tracks.Count > 0 && tracks[0].StartId != track.StartId)
                tracks.Clear();
            tracks.Add(track);
        }
    }

    internal static void Complete(World world, Entity animation) => GraphFieldOutputs<T>.Build(world, animation);

    internal static void Release(World world, Entity animation)
    {
        GraphFieldOutputs<T>.Release(world, animation);
        world.Set(animation, default(GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>));
    }
}

internal static class GraphFieldOutputs<T> where T : unmanaged
{
    internal static unsafe void Remove(World world, Entity animation, int leader)
    {
        int count = PropertyAnimation.BufferCount<GraphFieldOutput<T>>(world, animation);
        for (int outputIndex = 0; outputIndex < count; outputIndex++)
        {
            GraphFieldOutput<T> output = PropertyAnimation.ReadBufferItem<GraphFieldOutput<T>>(world, animation, outputIndex);
            if (output.Leader != leader)
                continue;
            for (int index = output.First; index < output.First + output.Count; index++)
            {
                int trackIndex = PropertyAnimation.ReadBufferItem<GraphFieldTrack<T>>(world, animation, index).Track;
                Track<T> track = PropertyAnimation.ReadTrack<T>(world, animation, trackIndex);
                if (track.MotionInitialized)
                    MotionAccesses<T>.Items[track.MotionType - 1].Release(world, animation, track.MotionIndex);
                track.MotionInitialized = false;
                track.SourceActive = false;
                track.Finished = true;
                PropertyAnimation.WriteTrack(world, animation, trackIndex, in track);
            }
            {
                using var outputs = world.GetBuffer<GraphFieldOutput<T>>(animation);
                outputs.RemoveAtSwapBack(outputIndex);
            }
            return;
        }
    }

    internal static void Release(World world, Entity animation)
    {
        var targets = new HashSet<Entity>();
        {
            using var tracks = world.GetBuffer<Track<T>>(animation);
            for (int index = 0; index < tracks.Count; index++)
            {
                ref Track<T> track = ref tracks[index];
                if (!track.FromGraph)
                    continue;
                targets.Add(track.Target);
                track.SourceActive = false;
            }
        }
        foreach (Entity stored in targets)
        {
            Entity target = stored == Entity.Null ? animation : stored;
            if (!world.IsAlive(target) || !world.Has<Buffer<PropertySource<T>>>(target))
                continue;
            Entity source = animation;
            {
                using var sources = world.GetBuffer<PropertySource<T>>(target);
                for (int index = sources.Count - 1; index >= 0; index--)
                    if (sources[index].Animation == source)
                        sources.RemoveAtSwapBack(index);
            }
            PropertyAnimation.RefreshSources<T>(world, target);
        }
        {
            using var fieldOutputs = world.GetBuffer<GraphFieldOutput<T>>(animation);
            fieldOutputs.Clear();
        }
        {
            using var tracks = world.GetBuffer<GraphFieldTrack<T>>(animation);
            tracks.Clear();
        }
    }

    internal static void Build(World world, Entity animation)
    {
        if (!world.Has<Buffer<GraphFieldOutput<T>>>(animation))
            world.AddBuffer<GraphFieldOutput<T>>(animation);
        if (!world.Has<Buffer<GraphFieldTrack<T>>>(animation))
            world.AddBuffer<GraphFieldTrack<T>>(animation);
        var values = new List<Track<T>>();
        {
            using var tracks = world.ReadBuffer<Track<T>>(animation);
            foreach (Track<T> track in tracks.AsSpan())
                values.Add(track);
        }
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
            if (!world.Has<Buffer<PropertySource<T>>>(target))
                world.AddBuffer<PropertySource<T>>(target);
            Entity source = animation;
            {
                using var sources = world.GetBuffer<PropertySource<T>>(target);
                for (int index = sources.Count - 1; index >= 0; index--)
                    if (sources[index].Animation == source)
                        sources.RemoveAtSwapBack(index);
            }
        }
        {
            using var fieldOutputs = world.GetBuffer<GraphFieldOutput<T>>(animation);
            fieldOutputs.Clear();
        }
        {
            using var tracks = world.GetBuffer<GraphFieldTrack<T>>(animation);
            tracks.Clear();
        }
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
            {
                using var buffer = world.GetBuffer<GraphFieldTrack<T>>(animation);
                first = buffer.Count;
            }
            foreach (int track in tracks)
            {
                var value = new GraphFieldTrack<T> { Track = track };
                {
                    using var buffer = world.GetBuffer<GraphFieldTrack<T>>(animation);
                    buffer.Add(value);
                }
            }
            var output = new GraphFieldOutput<T> { Id = id, Leader = tracks[0], First = first, Count = tracks.Count };
            {
                using var buffer = world.GetBuffer<GraphFieldOutput<T>>(animation);
                buffer.Add(output);
            }
            Track<T> leader = values[tracks[0]];
            Entity target = leader.Target == Entity.Null ? animation : leader.Target;
            var source = new PropertySource<T> { Animation = animation, Channel = leader.Channel, Layer = leader.Layer, Track = tracks[0] };
            {
                using var sources = world.GetBuffer<PropertySource<T>>(target);
                sources.Add(source);
            }
        }
        foreach (Entity target in targets)
            PropertyAnimation.RefreshSources<T>(world, target);
        foreach ((int _, List<int> indices) in outputs)
        {
            var update = (Indices: indices, Source: 0);
            {
                using var tracks = world.GetBuffer<Track<T>>(animation);
                update.Source = tracks[update.Indices[0]].SourceIndex;
                foreach (int index in update.Indices)
                    tracks[index].SourceIndex = update.Source;
            }
        }
    }
}
