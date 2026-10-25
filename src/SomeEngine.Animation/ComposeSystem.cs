using System.Buffers;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation;

/// <summary>Blends the frame's field contributions after property evaluators run.</summary>
public struct ComposeSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _targets;
    private QueryHandle _sources;
    private FieldWrite<T>[] _writes = [];
    private CompositionTargets _compositionTargets = null!;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _compositionTargets = new();
        _targets = context.World.Query(context.World.QueryDefinition()
            .WriteBuffer<Contribution<T>>().Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<BaseValue<T>>(QueryAccess.Read).OptionalBuffer<PropertySource<T>>(QueryAccess.Read)
            .OptionalBuffer<LayerChannel>(QueryAccess.Read));
        _sources = context.World.Query(context.World.QueryDefinition().WriteBuffer<PropertySource<T>>()
            .WriteBuffer<Contribution<T>>().Optional<AnimationLayers>(QueryAccess.Read)
            .ReadBuffer<PropertyChannelRange<T>>().OptionalBuffer<LayerChannel>(QueryAccess.Read).OptionalBuffer<BaseValue<T>>(QueryAccess.Read));
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        if (!world.HasQueryMembers(_targets)) return;
        var composition = (World: world, WeightChannels: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0, Targets: _compositionTargets);
        world.ExecuteQuery(_targets, context.LastSystemVersion, ref composition, &ComposeDynamic);
        world.ExecuteQuery(_sources, context.LastSystemVersion, ref composition, &ComposeFixed);
        FieldWrites<T>.Flush(world, composition.Writes, ref composition.Count);
        _writes = composition.Writes;

        static void ComposeDynamic(QueryCursor rows, ref (World World, bool WeightChannels, FieldWrite<T>[] Writes, int Count, CompositionTargets Targets) composition)
        {
            CompositionTargets targets = composition.Targets;
            targets.Count = 0;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (chunk.HasBuffer<PropertySource<T>>()) continue;
                foreach (QueryRow row in chunk.Rows)
                    if (row.ReadBuffer<Contribution<T>>().Count > 0)
                        targets.Add(row);
            }
            if (targets.Count == 0) return;
            var work = new ComposeDynamicRanges(targets, composition.WeightChannels);
            if (WorldJobs.TryGetScheduler(composition.World, out Scheduler? scheduler))
                scheduler.ExecuteParallel(work, targets.Count);
            else
                for (int index = 0; index < targets.Count; index++)
                    work.Execute(index);
            for (int index = 0; index < targets.Count; index++)
            {
                CompositionTarget target = targets.Items[index];
                for (int writeIndex = 0; writeIndex < target.WriteCount; writeIndex++)
                {
                    FieldWrite<T> write = target.Writes[writeIndex];
                    FieldWrites<T>.Add(ref composition.Writes, ref composition.Count, write.Target, write.Channel, write.Value);
                }
            }
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (chunk.HasBuffer<PropertySource<T>>()) continue;
                foreach (QueryRow row in chunk.Rows)
                {
                    DynamicBuffer<Contribution<T>> buffer = row.Buffer<Contribution<T>>();
                    for (int index = buffer.Count - 1; index >= 0; index--)
                        if ((buffer.Read(index).Channel.Component == ComponentId<AnimationLayers>.Value) == composition.WeightChannels)
                            buffer.SwapRemoveAt(index);
                }
            }
            targets.Count = 0;
        }
    }
    private static void ComposeFixed(QueryCursor rows, ref (World World, bool WeightChannels, FieldWrite<T>[] Writes, int Count, CompositionTargets Targets) composition)
    {
        CompositionTargets targets = composition.Targets;
        targets.Count = 0;
        foreach (QueryChunkView chunk in rows.Chunks)
        {
            int rowIndex = 0;
            foreach (QueryRow row in chunk.Rows)
            {
                CompositionTarget target = targets.Add(row);
                target.BasesChanged = chunk.HasBuffer<BaseValue<T>>() && chunk.RowBufferChangedSinceLastSystemVersion<BaseValue<T>>(rowIndex);
                target.LayersChanged = chunk.Has<AnimationLayers>() && chunk.RowChangedSinceLastSystemVersion<AnimationLayers>(rowIndex);
                target.MaskChanged = target.LayersChanged || chunk.HasBuffer<LayerChannel>() && chunk.RowBufferChangedSinceLastSystemVersion<LayerChannel>(rowIndex);
                rowIndex++;
            }
        }
        if (targets.Count == 0) return;
        var work = new ComposeFixedRanges(targets, composition.WeightChannels);
        if (WorldJobs.TryGetScheduler(composition.World, out Scheduler? scheduler))
            scheduler.ExecuteParallel(work, targets.Count);
        else
            for (int index = 0; index < targets.Count; index++)
                work.Execute(index);
        int targetIndex = 0;
        foreach (QueryRow row in rows.Rows)
        {
            CompositionTarget target = targets.Items[targetIndex++];
            target.Sources.AsSpan(0, target.SourceCount).CopyTo(row.Buffer<PropertySource<T>>().AsSpan());
            DynamicBuffer<Contribution<T>> contributions = row.Buffer<Contribution<T>>();
            for (int index = contributions.Count - 1; index >= 0; index--)
                if ((contributions.Read(index).Channel.Component == ComponentId<AnimationLayers>.Value) == composition.WeightChannels)
                    contributions.SwapRemoveAt(index);
            for (int index = 0; index < target.WriteCount; index++)
            {
                FieldWrite<T> write = target.Writes[index];
                FieldWrites<T>.Add(ref composition.Writes, ref composition.Count, write.Target, write.Channel, write.Value);
            }
        }
        targets.Count = 0;
    }

    private readonly struct ComposeFixedRanges(CompositionTargets targets, bool weightChannels) : IJobParallelFor
    {
        public void Execute(int index) => ComposeFixedTarget(targets.Items[index], weightChannels);
    }

    private static void ComposeFixedTarget(CompositionTarget target, bool weightChannels)
    {
        bool hasLayers = target.HasLayers;
        bool basesChanged = target.BasesChanged;
        bool layersChanged = target.LayersChanged;
        bool maskChanged = target.MaskChanged;
        Span<PropertySource<T>> sources = target.Sources.AsSpan(0, target.SourceCount);
        Span<Contribution<T>> dynamic = target.Contributions.AsSpan(0, target.ContributionCount);
        Sort(dynamic);
        ReadOnlySpan<BaseValue<T>> bases = target.Bases.AsSpan(0, target.BaseCount);
        AnimationLayers layers = target.Layers;
        ReadOnlySpan<PropertyChannelRange<T>> ranges = target.Ranges.AsSpan(0, target.RangeCount);
        int rangeIndex = 0, dynamicIndex = 0;
        while (rangeIndex < ranges.Length || dynamicIndex < dynamic.Length)
        {
            Channel<T> channel = dynamicIndex == dynamic.Length || rangeIndex < ranges.Length &&
                CompareChannels(ranges[rangeIndex].Channel, dynamic[dynamicIndex].Channel) <= 0
                ? ranges[rangeIndex].Channel : dynamic[dynamicIndex].Channel;
            int fixedStart = 0, fixedIndex = 0, baseIndex = -1;
            if (rangeIndex < ranges.Length && ranges[rangeIndex].Channel == channel)
            {
                PropertyChannelRange<T> range = ranges[rangeIndex++];
                fixedStart = range.FirstSource;
                fixedIndex = fixedStart + range.SourceCount;
                baseIndex = sources[fixedStart].BaseIndex;
            }
            int dynamicStart = dynamicIndex;
            while (dynamicIndex < dynamic.Length && dynamic[dynamicIndex].Channel == channel) dynamicIndex++;
            bool weightChannel = channel.Component == ComponentId<AnimationLayers>.Value;
            if (weightChannel != weightChannels) continue;
            bool dirty = basesChanged || layersChanged || maskChanged || dynamicStart != dynamicIndex;
            if (baseIndex < 0)
                for (int index = 0; index < bases.Length; index++)
                    if (bases[index].Channel == channel) { baseIndex = index; break; }
            bool hasBase = baseIndex >= 0;
            T basis = hasBase ? bases[baseIndex].Value : default;
            bool hasValue = hasBase;
            for (int index = fixedStart; index < fixedIndex; index++)
            {
                dirty |= sources[index].Dirty || sources[index].HadDynamic;
                if (maskChanged) sources[index].Selected = channel.Component == ComponentId<AnimationLayers>.Value || !hasLayers || Includes(target, channel, layers.Items[sources[index].Layer].Mask);
                if (!hasValue && sources[index].Active) { basis = sources[index].Value; hasValue = true; }
            }
            if (!hasValue && dynamicStart != dynamicIndex) { basis = dynamic[dynamicStart].Value; hasValue = true; }
            if (!dirty || !hasValue) continue;
            T result = basis;
            int fixedValue = fixedStart, dynamicValue = dynamicStart;
            bool contributed = false, included = false;
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
                    selected = !hasLayers || weightChannel || Includes(target, channel, layers.Items[contribution.Layer].Mask);
                }
                DevelopmentCheck.Require(!contributed || Compare(previous, contribution) != 0,
                    "Sources in the same field and layer require distinct Source and Output identifiers.");
                previous = contribution;
                contributed = true;
                if (!selected) continue;
                included = true;
                AnimationLayer layer = hasLayers && !weightChannel ? layers.Items[contribution.Layer] : new() { Weight = 1 };
                T value = contribution.Value;
                if (endpoints != TrackEndpoints.Values && layer.Mode == LayerMode.Additive)
                {
                    T displacement = TOps.Subtract(in value, in reference);
                    value = TOps.Add(in basis, in displacement);
                }
                result = PropertyAnimation.Blend<T, TOps>(in result, in value, in basis, hasBase, in layer);
            }
            if (hasBase || included) FieldWrites<T>.Add(ref target.Writes, ref target.WriteCount, target.Entity, channel, in result);
            for (int index = fixedStart; index < fixedIndex; index++)
            { sources[index].Dirty = false; sources[index].HadDynamic = index == fixedStart && dynamicStart != dynamicIndex; }
        }
        if (basesChanged)
        {
            foreach (ref readonly BaseValue<T> basis in bases)
            {
                if ((basis.Channel.Component == ComponentId<AnimationLayers>.Value) != weightChannels) continue;
                bool hasSource = false;
                foreach (PropertySource<T> source in sources) if (source.Channel == basis.Channel) { hasSource = true; break; }
                if (!hasSource)
                    foreach (Contribution<T> value in dynamic) if (value.Channel == basis.Channel) { hasSource = true; break; }
                if (!hasSource) FieldWrites<T>.Add(ref target.Writes, ref target.WriteCount, target.Entity, basis.Channel, in basis.Value);
            }
        }
    }

    private sealed class CompositionTarget
    {
        internal Entity Entity;
        internal Contribution<T>[] Contributions = [];
        internal PropertySource<T>[] Sources = [];
        internal PropertyChannelRange<T>[] Ranges = [];
        internal BaseValue<T>[] Bases = [];
        internal LayerChannel[] Channels = [];
        internal FieldWrite<T>[] Writes = new FieldWrite<T>[4];
        internal AnimationLayers Layers;
        internal int ContributionCount, BaseCount, ChannelCount, WriteCount, SourceCount, RangeCount;
        internal bool HasLayers, HasChannels, BasesChanged, LayersChanged, MaskChanged;
    }

    private sealed class CompositionTargets
    {
        internal readonly List<CompositionTarget> Items = [];
        internal int Count;

        internal CompositionTarget Add(QueryRow row)
        {
            if (Count == Items.Count) Items.Add(new());
            CompositionTarget target = Items[Count++];
            target.Entity = row.Entity;
            target.ContributionCount = Copy(ref target.Contributions, row.ReadBuffer<Contribution<T>>().AsSpan());
            target.BaseCount = row.HasBuffer<BaseValue<T>>()
                ? Copy(ref target.Bases, row.ReadBuffer<BaseValue<T>>().AsSpan()) : 0;
            target.HasChannels = row.HasBuffer<LayerChannel>();
            target.ChannelCount = target.HasChannels
                ? Copy(ref target.Channels, row.ReadBuffer<LayerChannel>().AsSpan()) : 0;
            target.HasLayers = row.Has<AnimationLayers>();
            target.Layers = target.HasLayers ? row.Read<AnimationLayers>() : default;
            target.SourceCount = row.HasBuffer<PropertySource<T>>()
                ? Copy(ref target.Sources, row.ReadBuffer<PropertySource<T>>().AsSpan()) : 0;
            target.RangeCount = row.HasBuffer<PropertyChannelRange<T>>()
                ? Copy(ref target.Ranges, row.ReadBuffer<PropertyChannelRange<T>>().AsSpan()) : 0;
            target.WriteCount = 0;
            return target;
        }

        private static int Copy<TElement>(ref TElement[] destination, ReadOnlySpan<TElement> values)
        {
            if (destination.Length < values.Length) Array.Resize(ref destination, values.Length);
            values.CopyTo(destination);
            return values.Length;
        }
    }

    private readonly struct ComposeDynamicRanges(CompositionTargets targets, bool weightChannels) : IJobParallelFor
    {
        public void Execute(int index) => ComposeCompositionTarget(targets.Items[index], weightChannels);
    }

    private static bool Includes(CompositionTarget target, Channel<T> channel, LayerMask mask)
    {
        if (mask.AllChannels) return true;
        DevelopmentCheck.Require(target.HasChannels, "A channel mask requires a LayerChannel buffer on its target.");
        DevelopmentCheck.Require(mask.Start <= target.ChannelCount && mask.Count <= target.ChannelCount - mask.Start,
            "A layer mask must fit its target's LayerChannel buffer.");
        for (int index = mask.Start; index < mask.Start + mask.Count; index++)
            if (target.Channels[index].Component == channel.Component && target.Channels[index].Field == channel.Field)
                return true;
        return false;
    }

    private static void ComposeCompositionTarget(CompositionTarget target, bool weightChannels)
    {
        Span<Contribution<T>> contributions = target.Contributions.AsSpan(0, target.ContributionCount);
        Sort(contributions);
        bool hasLayers = target.HasLayers;
        AnimationLayers layers = target.Layers;
        ReadOnlySpan<BaseValue<T>> bases = target.Bases.AsSpan(0, target.BaseCount);
        for (int i = 0; i < contributions.Length;)
        {
            Channel<T> channel = contributions[i].Channel;
            bool weightChannel = channel.Component == ComponentId<AnimationLayers>.Value;
            if (weightChannel != weightChannels)
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
                if (Includes(target, channel, layer.Mask))
                {
                    result = PropertyAnimation.Blend<T, TOps>(in result, in contribution.Value, in basis, hasBase, in layer);
                    includesValue = true;
                }
            }
            while (i < contributions.Length && contributions[i].Channel == channel);
            if (hasBase || includesValue)
                FieldWrites<T>.Add(ref target.Writes, ref target.WriteCount, target.Entity, channel, in result);
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
