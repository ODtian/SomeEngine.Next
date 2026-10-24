using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation;

/// <summary>Ordered layer definitions on the entity whose fields are animated.</summary>
public struct AnimationLayers : IComponent
{
    public LayerArray Items;
    public byte Count;
}

[InlineArray(8)]
public struct LayerArray
{
    private AnimationLayer _item;
}

public struct AnimationLayer
{
    public LayerMode Mode;
    public float Weight;
    public LayerMask Mask;
}

public enum LayerMode : byte { Override, Additive }

/// <summary>A range in the target's LayerChannel buffer. The default mask includes every channel.</summary>
public readonly struct LayerMask
{
    public readonly int Start;
    public readonly int Count;
    private readonly bool _selectChannels;

    public LayerMask(int start, int count)
    {
        DevelopmentCheck.Require(start >= 0 && count >= 0, "A layer mask channel range must be nonnegative.");
        Start = start;
        Count = count;
        _selectChannels = true;
    }

    public bool AllChannels => !_selectChannels;
}

/// <summary>A field selected by an animation layer mask.</summary>
[BufferCapacity(8)]
public struct LayerChannel : IBufferElement
{
    public ushort Component;
    public ushort Offset;
    public uint Field;

    public static LayerChannel Of<T>(Channel<T> channel) where T : unmanaged =>
        new() { Component = channel.Component, Offset = channel.Offset, Field = channel.Field };
}
