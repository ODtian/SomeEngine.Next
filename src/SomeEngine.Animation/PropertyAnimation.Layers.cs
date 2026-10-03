using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class PropertyAnimation
{
    /// <summary>Sets the caller's reference value for an animated field.</summary>
    public static void SetBase<T>(World world, Entity target, Channel<T> channel, T value) where T : unmanaged
    {
        if (!world.HasBuffer<BaseValue<T>>(target))
            world.AddBuffer<BaseValue<T>>(target);
        var basis = new BaseValue<T> { Channel = channel, Value = value };
        world.ExecuteBufferWrite<BaseValue<T>, BaseValue<T>>(target, ref basis,
            static (DynamicBuffer<BaseValue<T>> values, ref BaseValue<T> basis) =>
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values.Read(i).Channel != basis.Channel)
                        continue;
                    values[i] = basis;
                    return;
                }
                values.Add(basis);
            });
    }

    internal static bool ReadBase<T>(World world, Entity target, Channel<T> channel, ref T value) where T : unmanaged
    {
        if (!world.HasBuffer<BaseValue<T>>(target))
            return false;
        var field = (Channel: channel, Value: value, Found: false);
        world.ExecuteBufferRead<BaseValue<T>, (Channel<T> Channel, T Value, bool Found)>(target, ref field,
            static (BufferView<BaseValue<T>> values, ref (Channel<T> Channel, T Value, bool Found) field) =>
            {
                for (int i = 0; i < values.Count; i++)
                {
                    BaseValue<T> basis = values[i];
                    if (basis.Channel != field.Channel)
                        continue;
                    field.Value = basis.Value;
                    field.Found = true;
                    return;
                }
            });
        value = field.Value;
        return field.Found;
    }

    internal static void WriteSingle<T, TOps>(World world, Entity target, Channel<T> channel, byte layerIndex, T value)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        if (channel.Component != ComponentId<AnimationLayers>.Value && world.Has<AnimationLayers>(target))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(target);
            DevelopmentCheck.Require(layers.Count <= 8 && layerIndex < layers.Count,
                "A property track requires a defined animation layer.");
            AnimationLayer layer = layers.Items[layerIndex];
            T basis = value;
            bool hasBase = ReadBase(world, target, channel, ref basis);
            if (Includes(world, target, channel, layer.Mask))
                value = Blend<T, TOps>(in basis, in value, in basis, hasBase, in layer);
            else if (hasBase)
                value = basis;
            else
                return;
        }
        Unsafe.WriteUnaligned(ref world.WriteBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>()), value);
    }

    internal static bool Includes<T>(World world, Entity target, Channel<T> channel, LayerMask mask) where T : unmanaged
    {
        if (mask.AllChannels)
            return true;
        DevelopmentCheck.Require(world.HasBuffer<LayerChannel>(target), "A channel mask requires a LayerChannel buffer on its target.");
        var selection = (Channel: channel, Mask: mask, Found: false);
        world.ExecuteBufferRead<LayerChannel, (Channel<T> Channel, LayerMask Mask, bool Found)>(target, ref selection,
            static (BufferView<LayerChannel> channels, ref (Channel<T> Channel, LayerMask Mask, bool Found) selection) =>
            {
                LayerMask mask = selection.Mask;
                DevelopmentCheck.Require(mask.Start <= channels.Count && mask.Count <= channels.Count - mask.Start,
                    "A layer mask must fit its target's LayerChannel buffer.");
                for (int i = mask.Start; i < mask.Start + mask.Count; i++)
                {
                    LayerChannel field = channels[i];
                    if (field.Component == selection.Channel.Component && field.Offset == selection.Channel.Offset)
                    {
                        selection.Found = true;
                        return;
                    }
                }
            });
        return selection.Found;
    }

    internal static T Blend<T, TOps>(in T result, in T value, in T basis, bool hasBase, in AnimationLayer layer)
        where T : unmanaged where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(hasBase || layer.Mode == LayerMode.Override && layer.Weight == 1,
            "Weighted and additive animation layers require PropertyAnimation.SetBase for the field.");
        DevelopmentCheck.Require(float.IsFinite(layer.Weight) && layer.Weight >= 0 && layer.Weight <= 1,
            "An animation layer weight must be between zero and one.");
        DevelopmentCheck.Require(layer.Mode is LayerMode.Override or LayerMode.Additive,
            "An animation layer requires a defined blend mode.");
        if (layer.Mode == LayerMode.Override)
            return TOps.Lerp(in result, in value, layer.Weight);
        T difference = TOps.Subtract(in value, in basis);
        T displacement = TOps.Scale(in difference, layer.Weight);
        return TOps.Add(in result, in displacement);
    }
}
