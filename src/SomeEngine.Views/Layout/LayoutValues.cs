namespace SomeEngine.Layout;

public readonly record struct Size(float Width, float Height)
{
    public static Size Unbounded => new(float.PositiveInfinity, float.PositiveInfinity);
    public static Size Auto => new(float.NaN, float.NaN);
}

/// <summary>The permitted outer size of a node, including its padding.</summary>
public readonly record struct Constraints(Size Min, Size Max)
{
    public static Constraints Fixed(Size size) => new(size, size);
    public Size Constrain(Size size) => new(
        Math.Clamp(size.Width, Min.Width, Max.Width),
        Math.Clamp(size.Height, Min.Height, Max.Height));
}
