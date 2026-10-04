using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Caller-supplied value, inclusive limits, and edit quantization.</summary>
public record struct ValueRange(float Value, float Minimum = 0, float Maximum = 1, float Step = 0) : IComponent
{
    /// <summary>The caller's value expressed as a fraction of the range for presentation.</summary>
    public readonly float Ratio => Maximum == Minimum ? 0 :
        (float)(((double)Value - Minimum) / ((double)Maximum - Minimum));
}

/// <summary>Direction and canvas distance corresponding to the complete value range.</summary>
public record struct ValueDrag(Vector2 Direction, float Distance) : IComponent;

/// <summary>The input stage's most recent edit and its increasing change count.</summary>
public struct ValueEdit : IComponent
{
    public float Value;
    public uint Count;
}

/// <summary>The edit count consumed by the caller's Edited read.</summary>
public struct ValueEditSeen : IComponent
{
    public uint Count;
}
