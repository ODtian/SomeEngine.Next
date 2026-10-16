using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    internal static void FinishSource<T>(World world, Entity animation, in Track<T> track) where T : unmanaged
    {
        if (HoldsLayerValue(world, animation, in track)) return;
        Entity target = track.Target == Entity.Null ? animation : track.Target;
        if (!world.HasBuffer<PropertySource<T>>(target)) return;
        int index = track.SourceIndex;
        world.ExecuteBufferWrite<PropertySource<T>, int>(target, ref index, static (sources, ref index) =>
        {
            PropertySource<T> source = sources.Read(index);
            if (!source.Active) return;
            source.Active = false;
            source.Dirty = true;
            sources[index] = source;
        });
    }

    private static class SourceQueries<T> where T : unmanaged
    {
        private static readonly ConditionalWeakTable<World, SourceQuery> Queries = new();
        internal static QueryHandle For(World world) => Queries.GetValue(world, static world => new SourceQuery(world)).Handle;
        private sealed class SourceQuery(World world)
        {
            internal readonly QueryHandle Handle = world.Query(world.QueryDefinition().ReadBuffer<PropertySource<T>>().OptionalBuffer<Track<T>>(QueryAccess.Read));
        }
    }

    /// <summary>Reserves the transient contribution batch used by external value systems.</summary>
    public static void DeclareContributions<T>(World world, Entity target, int capacity = 4) where T : unmanaged
    {
        if (!world.HasBuffer<Contribution<T>>(target)) world.AddBuffer<Contribution<T>>(target);
        world.ExecuteBufferWrite<Contribution<T>, int>(target, ref capacity,
            static (contributions, ref capacity) => contributions.EnsureCapacity(capacity));
        RefreshSources<T>(world, target);
    }

    public static void Contribute<T>(World world, Entity target, in Contribution<T> contribution) where T : unmanaged
    {
        Contribution<T> value = contribution;
        world.ExecuteBufferWrite<Contribution<T>, Contribution<T>>(target, ref value,
            static (contributions, ref value) => contributions.Add(value));
    }

    /// <summary>Releases and removes a source relationship, then resolves the remaining field range.</summary>
    public static unsafe void RemoveSource<T>(World world, Entity target, Entity animation, uint output) where T : unmanaged
    {
        Channel<T> channel = default;
        for (int index = BufferCount<PropertySource<T>>(world, target) - 1; index >= 0; index--)
        {
            PropertySource<T> source = ReadSource<T>(world, target, index);
            if (source.Animation != animation || source.Track != output) continue;
            Track<T> track = ReadTrack<T>(world, animation, source.Track);
            if (track.MotionInitialized) MotionAccesses<T>.Items[track.MotionType - 1].Release(world, animation, track.MotionIndex);
            if (!track.FromGraph)
            {
                Timeline timeline = world.Read<Timeline>(animation);
                RemoveDirectBoundary<T>(world, animation, source.Track, in timeline);
            }
            track.SourceActive = false;
            track.MotionInitialized = false;
            track.Finished = true;
            WriteTrack(world, animation, source.Track, in track);
            channel = source.Channel;
            world.ExecuteBufferWrite<PropertySource<T>, int>(target, ref index, static (sources, ref index) => sources.SwapRemoveAt(index));
        }
        RefreshSources<T>(world, target);
        T basis = default;
        if (ReadBase(world, target, channel, ref basis))
        {
            DeclareContributions<T>(world, target);
            SetBase(world, target, channel, basis);
        }
    }

    /// <summary>Resolves source order, field ranges, bases, masks, and writeback after explicit relationship changes.</summary>
    public static void RefreshSources<T>(World world, Entity target) where T : unmanaged
    {
        if (!world.HasBuffer<PropertySource<T>>(target)) return;
        var selection = (Layers: world.Has<AnimationLayers>(target), Multiple: false, Count: 0);
        world.ExecuteBufferRead<PropertySource<T>, (bool Layers, bool Multiple, int Count)>(target, ref selection,
            static (sources, ref selection) =>
            {
                selection.Count = sources.Count;
                for (int index = 0; index < sources.Count; index++)
                    for (int previous = 0; previous < index; previous++)
                        if (sources[index].Channel == sources[previous].Channel) selection.Multiple = true;
            });
        if ((selection.Layers || selection.Multiple) && !world.HasBuffer<Contribution<T>>(target))
            world.AddBuffer<Contribution<T>>(target);
        if (world.HasBuffer<Contribution<T>>(target))
            world.ExecuteBufferWrite<Contribution<T>, int>(target, ref selection.Count,
                static (contributions, ref count) => contributions.EnsureCapacity(count));
        world.ExecuteBufferWrite<PropertySource<T>>(target,
            static sources =>
            {
                Span<PropertySource<T>> values = sources.AsSpan();
                for (int index = 1; index < values.Length; index++)
                {
                    PropertySource<T> value = values[index];
                    int previous = index - 1;
                    while (previous >= 0 && CompareSources(in values[previous], in value) > 0)
                    { values[previous + 1] = values[previous]; previous--; }
                    values[previous + 1] = value;
                }
            });
        bool direct = !world.HasBuffer<Contribution<T>>(target);
        for (int index = 0; index < selection.Count; index++)
        {
            PropertySource<T> source = ReadSource<T>(world, target, index);
            source.Direct = direct;
            source.Dirty = true;
            source.BaseIndex = BaseIndex(world, target, source.Channel);
            source.Selected = SourceSelected(world, target, source.Channel, source.Layer);
            var item = (Index: index, Source: source);
            world.ExecuteBufferWrite<PropertySource<T>, (int Index, PropertySource<T> Source)>(target, ref item,
                static (sources, ref item) => sources[item.Index] = item.Source);
            var trackIndex = (Track: source.Track, Source: index);
            world.ExecuteBufferWrite<Track<T>, (int Track, int Source)>(source.Animation, ref trackIndex,
                static (tracks, ref trackIndex) => tracks[trackIndex.Track].SourceIndex = trackIndex.Source);
        }
    }

    internal static PropertySource<T> ReadSource<T>(World world, Entity target, int index) where T : unmanaged
    {
        var item = (Index: index, Source: default(PropertySource<T>));
        world.ExecuteBufferRead<PropertySource<T>, (int Index, PropertySource<T> Source)>(target, ref item,
            static (sources, ref item) => item.Source = sources[item.Index]);
        return item.Source;
    }

    internal static int BufferCount<TBuffer>(World world, Entity entity) where TBuffer : struct, IBufferElement
    {
        int count = 0;
        world.ExecuteBufferRead<TBuffer, int>(entity, ref count, static (buffer, ref count) => count = buffer.Count);
        return count;
    }

    internal static TBuffer ReadBufferItem<TBuffer>(World world, Entity entity, int index) where TBuffer : struct, IBufferElement
    {
        var item = (Index: index, Value: default(TBuffer));
        world.ExecuteBufferRead<TBuffer, (int Index, TBuffer Value)>(entity, ref item, static (buffer, ref item) => item.Value = buffer[item.Index]);
        return item.Value;
    }

    private static int BaseIndex<T>(World world, Entity target, Channel<T> channel) where T : unmanaged
    {
        if (!world.HasBuffer<BaseValue<T>>(target)) return -1;
        var search = (Channel: channel, Index: -1);
        world.ExecuteBufferRead<BaseValue<T>, (Channel<T> Channel, int Index)>(target, ref search,
            static (values, ref search) =>
            {
                for (int index = 0; index < values.Count; index++)
                    if (values[index].Channel == search.Channel) { search.Index = index; return; }
            });
        return search.Index;
    }

    internal static bool SourceSelected<T>(World world, Entity target, Channel<T> channel, byte layer) where T : unmanaged =>
        channel.Component == ComponentId<AnimationLayers>.Value || !world.Has<AnimationLayers>(target) ||
        Includes(world, target, channel, world.Read<AnimationLayers>(target).Items[layer].Mask);

    internal static int CompareSources<T>(in PropertySource<T> first, in PropertySource<T> second) where T : unmanaged
    {
        int order = first.Channel.Component.CompareTo(second.Channel.Component);
        if (order == 0) order = first.Channel.Field.CompareTo(second.Channel.Field);
        if (order == 0) order = first.Layer.CompareTo(second.Layer);
        if (order == 0) order = first.Animation.Index.CompareTo(second.Animation.Index);
        if (order == 0) order = first.Animation.Generation.CompareTo(second.Animation.Generation);
        return order == 0 ? first.Track.CompareTo(second.Track) : order;
    }
}
