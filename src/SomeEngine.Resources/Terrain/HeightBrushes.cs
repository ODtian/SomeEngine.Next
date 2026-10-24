namespace SomeEngine.Resources;

/// <summary>A value-type CPU brush, evaluated against one fixed input version.</summary>
public interface IHeightBrushCpu
{
    /// <summary>Samples written by this brush, in global X/Z sample coordinates.</summary>
    HeightRegion Range { get; }
    /// <summary>Additional input samples around each written page region.</summary>
    int ReadBorder { get; }
    /// <summary>Computes one new height using samples inside the supplied source region.</summary>
    float Apply(HeightFieldRead source, int x, int z);
}

/// <summary>Adds a height delta in metres throughout a sample region.</summary>
public readonly record struct HeightDeltaBrush(HeightRegion Range, float Delta) : IHeightBrushCpu
{
    public int ReadBorder => 0;
    public float Apply(HeightFieldRead source, int x, int z) => source[x, z] + Delta;
}
