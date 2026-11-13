namespace SomeEngine.Render.Features.Decals;
public enum MaterialDecalIndex : byte { FullScan, Xyz, Xy }
public sealed record MaterialDecalSettings
{
    public uint ReferenceCapacity { get; init; } = 1048576;
    public uint TileSize { get; init; } = 16;
    public uint DepthSliceCount { get; init; } = 16;
    public float NearDepth { get; init; } = .1f;
    public float FarDepth { get; init; } = 10000;
    public MaterialDecalIndex Index { get; init; } = MaterialDecalIndex.Xyz;
}
