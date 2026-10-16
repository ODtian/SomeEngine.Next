using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
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
        var removal = (World: world, Animation: animation, Output: output, Channel: default(Channel<T>));
        world.ExecuteBufferWrite<PropertySource<T>, (World World, Entity Animation, uint Output, Channel<T> Channel)>(target, ref removal,
            static (sources, ref removal) =>
            {
                for (int index = sources.Count - 1; index >= 0; index--)
                {
                    PropertySource<T> source = sources.Read(index);
                    if (source.Animation != removal.Animation || source.Track != removal.Output) continue;
                    Track<T> track = ReadTrack<T>(removal.World, removal.Animation, source.Track);
                    if (track.MotionInitialized) MotionAccesses<T>.Items[track.MotionType - 1].Release(removal.World, removal.Animation, track.MotionIndex);
                    track.SourceActive = false;
                    track.MotionInitialized = false;
                    track.Finished = true;
                    WriteTrack(removal.World, removal.Animation, source.Track, in track);
                    removal.Channel = source.Channel;
                    sources.SwapRemoveAt(index);
                }
            });
        RefreshSources<T>(world, target);
        T basis = default;
        if (ReadBase(world, target, removal.Channel, ref basis)) removal.Channel.Write(world, target, in basis);
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
        var refresh = (World: world, Target: target, Direct: !world.HasBuffer<Contribution<T>>(target));
        world.ExecuteBufferWrite<PropertySource<T>, (World World, Entity Target, bool Direct)>(target, ref refresh,
            static (sources, ref refresh) =>
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
                for (int index = 0; index < values.Length; index++)
                {
                    ref PropertySource<T> source = ref values[index];
                    source.Direct = refresh.Direct;
                    source.Dirty = true;
                    source.BaseIndex = BaseIndex(refresh.World, refresh.Target, source.Channel);
                    source.Selected = SourceSelected(refresh.World, refresh.Target, source.Channel, source.Layer);
                    var trackIndex = (Track: source.Track, Source: index);
                    refresh.World.ExecuteBufferWrite<Track<T>, (int Track, int Source)>(source.Animation, ref trackIndex,
                        static (tracks, ref trackIndex) => tracks[trackIndex.Track].SourceIndex = trackIndex.Source);
                }
            });
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
