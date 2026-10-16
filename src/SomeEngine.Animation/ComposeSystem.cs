using System.Buffers;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Blends the frame's field contributions after property evaluators run.</summary>
public struct ComposeSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _targets;
    private QueryHandle _sources;
    private FieldWrite<T>[] _writes;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _targets = context.World.Query(context.World.QueryDefinition()
            .WriteBuffer<Contribution<T>>().Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<BaseValue<T>>(QueryAccess.Read).OptionalBuffer<PropertySource<T>>(QueryAccess.Read));
        _sources = context.World.Query(context.World.QueryDefinition().WriteBuffer<PropertySource<T>>()
            .WriteBuffer<Contribution<T>>().Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LayerChannel>(QueryAccess.Read).OptionalBuffer<BaseValue<T>>(QueryAccess.Read));
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        var composition = (World: world, WeightChannels: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0);
        world.ExecuteQuery(_targets, context.LastSystemVersion, ref composition, &ComposeDynamic);
        world.ExecuteQuery(_sources, context.LastSystemVersion, ref composition, &ComposeFixed);
        _writes = composition.Writes;

        static void ComposeDynamic(QueryCursor rows, ref (World World, bool WeightChannels, FieldWrite<T>[] Writes, int Count) composition)
        {
            World world = composition.World;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (chunk.HasBuffer<PropertySource<T>>()) continue;
                bool hasBases = chunk.HasBuffer<BaseValue<T>>();
                foreach (QueryRow row in chunk.Rows)
                {
                    DynamicBuffer<Contribution<T>> buffer = row.Buffer<Contribution<T>>();
                    Span<Contribution<T>> contributions = buffer.AsSpan();
                    Sort(contributions);
                    bool hasLayers = row.Has<AnimationLayers>();
                    AnimationLayers layers = hasLayers ? row.Read<AnimationLayers>() : default;
                    ReadOnlySpan<BaseValue<T>> bases = hasBases ? row.ReadBuffer<BaseValue<T>>().AsSpan() : default;
                    for (int i = 0; i < contributions.Length;)
                    {
                        Channel<T> channel = contributions[i].Channel;
                        bool weightChannel = channel.Component == ComponentId<AnimationLayers>.Value;
                        if (weightChannel != composition.WeightChannels)
                        {
                            do
                            {
                                i++;
                            }
                            while (i < contributions.Length && contributions[i].Channel == channel);
                            continue;
                        }
                        T basis = contributions[i].Value;
                        bool hasBase = false;
                        for (int b = 0; b < bases.Length; b++)
                        {
                            if (bases[b].Channel != channel)
                                continue;
                            basis = bases[b].Value;
                            hasBase = true;
                            break;
                        }
                        T result = basis;
                        bool includesValue = false;
                        do
                        {
                            DevelopmentCheck.Require(i == 0 || Compare(contributions[i - 1], contributions[i]) != 0,
                                "Sources in the same field and layer require distinct Source and Output identifiers.");
                            Contribution<T> contribution = contributions[i++];
                            AnimationLayer layer = new() { Weight = 1 };
                            if (hasLayers && !weightChannel)
                            {
                                DevelopmentCheck.Require(layers.Count <= 8 && contribution.Layer < layers.Count,
                                    "A property contribution requires a defined animation layer.");
                                layer = layers.Items[contribution.Layer];
                            }
                            if (PropertyAnimation.Includes(world, row.Entity, channel, layer.Mask))
                            {
                                result = PropertyAnimation.Blend<T, TOps>(in result, in contribution.Value, in basis, hasBase, in layer);
                                includesValue = true;
                            }
                        }
                        while (i < contributions.Length && contributions[i].Channel == channel);
                        if (hasBase || includesValue)
                            FieldWrites<T>.Add(ref composition.Writes, ref composition.Count, row.Entity, channel, in result);
                    }
                    // Retain the buffer's capacity for the next frame's contributions.
                    for (int i = buffer.Count - 1; i >= 0; i--)
                    {
                        bool weightChannel = buffer.Read(i).Channel.Component == ComponentId<AnimationLayers>.Value;
                        if (weightChannel == composition.WeightChannels)
                            buffer.SwapRemoveAt(i);
                    }
                }
                FieldWrites<T>.Flush(world, composition.Writes, ref composition.Count);
            }
        }
    }

    private static void ComposeFixed(QueryCursor rows, ref (World World, bool WeightChannels, FieldWrite<T>[] Writes, int Count) composition)
    {
        World world = composition.World;
        foreach (QueryChunkView chunk in rows.Chunks)
        {
            bool hasBases = chunk.HasBuffer<BaseValue<T>>();
            bool hasLayers = chunk.Has<AnimationLayers>();
            bool hasMask = chunk.HasBuffer<LayerChannel>();
            int rowIndex = 0;
            foreach (QueryRow row in chunk.Rows)
            {
                bool basesChanged = hasBases && chunk.RowBufferChangedSinceLastSystemVersion<BaseValue<T>>(rowIndex);
                bool layersChanged = hasLayers && chunk.RowChangedSinceLastSystemVersion<AnimationLayers>(rowIndex);
                bool maskChanged = layersChanged || hasMask && chunk.RowBufferChangedSinceLastSystemVersion<LayerChannel>(rowIndex);
                rowIndex++;
                DynamicBuffer<PropertySource<T>> sourceBuffer = row.Buffer<PropertySource<T>>();
                Span<PropertySource<T>> sources = sourceBuffer.AsSpan();
                DynamicBuffer<Contribution<T>> dynamicBuffer = row.Buffer<Contribution<T>>();
                Span<Contribution<T>> dynamic = dynamicBuffer.AsSpan();
                Sort(dynamic);
                ReadOnlySpan<BaseValue<T>> bases = hasBases ? row.ReadBuffer<BaseValue<T>>().AsSpan() : default;
                AnimationLayers layers = hasLayers ? row.Read<AnimationLayers>() : default;
                int fixedIndex = 0, dynamicIndex = 0;
                while (fixedIndex < sources.Length || dynamicIndex < dynamic.Length)
                {
                    Channel<T> channel = dynamicIndex == dynamic.Length || fixedIndex < sources.Length &&
                        CompareChannels(sources[fixedIndex].Channel, dynamic[dynamicIndex].Channel) <= 0
                        ? sources[fixedIndex].Channel : dynamic[dynamicIndex].Channel;
                    int fixedStart = fixedIndex, dynamicStart = dynamicIndex;
                    while (fixedIndex < sources.Length && sources[fixedIndex].Channel == channel) fixedIndex++;
                    while (dynamicIndex < dynamic.Length && dynamic[dynamicIndex].Channel == channel) dynamicIndex++;
                    bool weightChannel = channel.Component == ComponentId<AnimationLayers>.Value;
                    if (weightChannel != composition.WeightChannels) continue;
                    bool dirty = basesChanged || layersChanged || maskChanged || dynamicStart != dynamicIndex;
                    int baseIndex = fixedStart < fixedIndex ? sources[fixedStart].BaseIndex : -1;
                    if (baseIndex < 0)
                        for (int index = 0; index < bases.Length; index++)
                            if (bases[index].Channel == channel) { baseIndex = index; break; }
                    bool hasBase = baseIndex >= 0;
                    T basis = hasBase ? bases[baseIndex].Value : default;
                    bool hasValue = hasBase;
                    for (int index = fixedStart; index < fixedIndex; index++)
                    {
                        dirty |= sources[index].Dirty || sources[index].HadDynamic;
                        if (maskChanged) sources[index].Selected = PropertyAnimation.SourceSelected(world, row.Entity, channel, sources[index].Layer);
                        if (!hasValue && sources[index].Active) { basis = sources[index].Value; hasValue = true; }
                    }
                    if (!hasValue && dynamicStart != dynamicIndex) { basis = dynamic[dynamicStart].Value; hasValue = true; }
                    if (!dirty || !hasValue) continue;
                    T result = basis;
                    int fixedValue = fixedStart, dynamicValue = dynamicStart;
                    bool contributed = false;
                    Contribution<T> previous = default;
                    while (fixedValue < fixedIndex || dynamicValue < dynamicIndex)
                    {
                        while (fixedValue < fixedIndex && !sources[fixedValue].Active) fixedValue++;
                        if (fixedValue == fixedIndex && dynamicValue == dynamicIndex) break;
                        Contribution<T> fixedContribution = fixedValue < fixedIndex ? new()
                        {
                            Channel = channel, Layer = sources[fixedValue].Layer, Value = sources[fixedValue].Value,
                            Source = sources[fixedValue].Animation, Output = (uint)sources[fixedValue].Track,
                        } : default;
                        bool useFixed = dynamicValue == dynamicIndex || fixedValue < fixedIndex && Compare(fixedContribution, dynamic[dynamicValue]) <= 0;
                        bool selected;
                        TrackEndpoints endpoints = TrackEndpoints.Values;
                        T reference = default;
                        Contribution<T> contribution;
                        if (useFixed)
                        {
                            contribution = fixedContribution;
                            selected = sources[fixedValue].Selected;
                            endpoints = sources[fixedValue].Endpoints;
                            reference = sources[fixedValue++].Reference;
                        }
                        else
                        {
                            contribution = dynamic[dynamicValue++];
                            selected = !hasLayers || weightChannel || PropertyAnimation.Includes(world, row.Entity, channel, layers.Items[contribution.Layer].Mask);
                        }
                        DevelopmentCheck.Require(!contributed || Compare(previous, contribution) != 0,
                            "Sources in the same field and layer require distinct Source and Output identifiers.");
                        previous = contribution;
                        contributed = true;
                        if (!selected) continue;
                        AnimationLayer layer = hasLayers && !weightChannel ? layers.Items[contribution.Layer] : new() { Weight = 1 };
                        T value = contribution.Value;
                        if (endpoints != TrackEndpoints.Values && layer.Mode == LayerMode.Additive)
                        {
                            T displacement = TOps.Subtract(in value, in reference);
                            value = TOps.Add(in basis, in displacement);
                        }
                        result = PropertyAnimation.Blend<T, TOps>(in result, in value, in basis, hasBase, in layer);
                    }
                    if (hasBase || contributed) FieldWrites<T>.Add(ref composition.Writes, ref composition.Count, row.Entity, channel, in result);
                    for (int index = fixedStart; index < fixedIndex; index++)
                    { sources[index].Dirty = false; sources[index].HadDynamic = index == fixedStart && dynamicStart != dynamicIndex; }
                }
                for (int index = dynamicBuffer.Count - 1; index >= 0; index--)
                    if ((dynamicBuffer.Read(index).Channel.Component == ComponentId<AnimationLayers>.Value) == composition.WeightChannels)
                        dynamicBuffer.SwapRemoveAt(index);
            }
            FieldWrites<T>.Flush(world, composition.Writes, ref composition.Count);
        }
    }

    private static int CompareChannels(Channel<T> first, Channel<T> second)
    {
        int order = first.Component.CompareTo(second.Component);
        return order == 0 ? first.Field.CompareTo(second.Field) : order;
    }

    private static void Sort(Span<Contribution<T>> contributions)
    {
        for (int i = 1; i < contributions.Length; i++)
        {
            Contribution<T> value = contributions[i];
            int before = i - 1;
            while (before >= 0 && Compare(contributions[before], value) > 0)
            {
                contributions[before + 1] = contributions[before];
                before--;
            }
            contributions[before + 1] = value;
        }
    }

    private static int Compare(Contribution<T> first, Contribution<T> second)
    {
        int order = first.Channel.Component.CompareTo(second.Channel.Component);
        if (order == 0)
            order = first.Channel.Field.CompareTo(second.Channel.Field);
        if (order == 0)
            order = first.Layer.CompareTo(second.Layer);
        if (order == 0)
            order = first.Source.Index.CompareTo(second.Source.Index);
        if (order == 0)
            order = first.Source.Generation.CompareTo(second.Source.Generation);
        return order != 0 ? order : first.Output.CompareTo(second.Output);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.ReleaseQuery(_targets);
        context.World.ReleaseQuery(_sources);
        ArrayPool<FieldWrite<T>>.Shared.Return(_writes);
    }
}
