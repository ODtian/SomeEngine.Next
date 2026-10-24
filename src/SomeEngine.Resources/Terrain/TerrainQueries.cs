using System.Numerics;

namespace SomeEngine.Resources;

public enum TerrainFieldKind : byte { Height, Voxel }

/// <summary>A field's type, Terrain-local identifier, and captured sample revision.</summary>
public readonly record struct TerrainFieldVersion(TerrainFieldKind Kind, int Id, ulong Revision);

public readonly record struct TerrainHeightQuery(HeightFieldRead Source, Vector2 Position, float Height, Vector3 Normal)
{
    public TerrainFieldVersion FieldVersion => Source.Version;
}

public readonly record struct TerrainDistanceQuery(VoxelFieldRead Source, Vector3 Position, float Distance, Vector3 Normal)
{
    public TerrainFieldVersion FieldVersion => Source.Version;
}

public readonly record struct TerrainSurfaceQuery(TerrainSurfaceRead Source, Vector3 Position, float Value, Vector3 Normal)
{
    public ulong Revision => Source.Revision;
    public ReadOnlyMemory<TerrainFieldVersion> FieldVersions => Source.FieldVersions;
}

/// <summary>Queries fixed CPU reads at terrain-local metre positions.</summary>
public static class TerrainQueries
{
    /// <summary>The captured range includes the normal's X/Z neighborhood, in metres.</summary>
    public static TerrainHeightQuery QueryHeight(this HeightFieldRead source, Vector2 position, float normalDistance)
    {
        Vector2 x = new(normalDistance, 0), z = new(0, normalDistance);
        Vector3 normal = Vector3.Normalize(new Vector3(
            source.Sample(position - x) - source.Sample(position + x), 2f * normalDistance,
            source.Sample(position - z) - source.Sample(position + z)));
        return new(source, position, source.Sample(position), normal);
    }

    /// <summary>Queries a field whose samples represent signed distance; the range includes the normal neighborhood.</summary>
    public static TerrainDistanceQuery QueryDistance(this VoxelFieldRead source, Vector3 position, float normalDistance)
    {
        Vector3 x = new(normalDistance, 0, 0), y = new(0, normalDistance, 0), z = new(0, 0, normalDistance);
        Vector3 normal = Vector3.Normalize(new Vector3(
            source.Sample(position + x) - source.Sample(position - x),
            source.Sample(position + y) - source.Sample(position - y),
            source.Sample(position + z) - source.Sample(position - z)));
        return new(source, position, source.Sample(position), normal);
    }

    /// <summary>Queries the captured scalar and its outward normal within the read's bounds.</summary>
    public static TerrainSurfaceQuery Query(this TerrainSurfaceRead source, Vector3 position, float normalDistance) =>
        new(source, position, source.Sample(position), source.GetNormal(position, normalDistance));
}
