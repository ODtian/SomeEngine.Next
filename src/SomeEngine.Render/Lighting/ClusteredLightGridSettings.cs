namespace SomeEngine.Render.Lighting;

/// <summary>View-space cell dimensions and bounded light-list capacity.</summary>
public sealed record ClusteredLightGridSettings
{
    public uint TileSize { get; init; } = 16;
    public uint DepthSliceCount { get; init; } = 16;
    public uint MaxLightsPerCell { get; init; } = 64;

    public void Validate()
    {
        if (TileSize is 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(TileSize));
        if (DepthSliceCount is 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(DepthSliceCount));
        if (MaxLightsPerCell is 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxLightsPerCell));
    }
}
