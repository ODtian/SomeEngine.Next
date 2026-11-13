namespace SomeEngine.Render.Features.Decals;

/// <summary>Screen tiles and logarithmic depth slices used by projected material volumes.</summary>
public sealed record MaterialDecalBinningSettings
{
    public uint TileSize { get; init; } = 16;
    public uint DepthSliceCount { get; init; } = 16;
    public float NearDepth { get; init; } = 0.1f;
    public float FarDepth { get; init; } = 10_000f;
}
