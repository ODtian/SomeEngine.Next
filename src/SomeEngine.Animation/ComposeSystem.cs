using System.Runtime.CompilerServices;
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

    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(context.World.QueryDefinition()
            .WriteBuffer<Contribution<T>>().Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<BaseValue<T>>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        var composition = (World: world, WeightChannels: _fields == TrackFields.LayerWeights);
        world.ExecuteQuery(_targets, context.LastSystemVersion, ref composition,
            static (QueryCursor rows, ref (World World, bool WeightChannels) composition) =>
        {
            World world = composition.World;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
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
                            Unsafe.WriteUnaligned(ref world.WriteBytes(row.Entity, channel.Component, channel.Offset, Unsafe.SizeOf<T>()), result);
                    }
                    // Retain the buffer's capacity for the next frame's contributions.
                    for (int i = buffer.Count - 1; i >= 0; i--)
                    {
                        bool weightChannel = buffer.Read(i).Channel.Component == ComponentId<AnimationLayers>.Value;
                        if (weightChannel == composition.WeightChannels)
                            buffer.SwapRemoveAt(i);
                    }
                }
            }
        });
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
            order = first.Channel.Offset.CompareTo(second.Channel.Offset);
        if (order == 0)
            order = first.Layer.CompareTo(second.Layer);
        if (order == 0)
            order = first.Source.Index.CompareTo(second.Source.Index);
        if (order == 0)
            order = first.Source.Generation.CompareTo(second.Source.Generation);
        return order != 0 ? order : first.Output.CompareTo(second.Output);
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_targets);
}
