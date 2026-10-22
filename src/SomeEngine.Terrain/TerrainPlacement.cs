using SomeEngine.Core.Math;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>A selected range of spatial chunks; maximum coordinates are exclusive.</summary>
public readonly record struct TerrainChunkRange(int MinX, int MinY, int MinZ, int Width, int Height, int Depth)
{
    public int MaxX => MinX + Width;
    public int MaxY => MinY + Height;
    public int MaxZ => MinZ + Depth;
}

public sealed class TerrainPlacement(Resources.Terrain terrain, TerrainChunkRange chunks,
    IReadOnlyList<Material> materials, int lod = 0)
{
    private TransformQvvs _transform = TransformQvvs.Identity;
    private IReadOnlyList<Material> _materials = materials.ToArray();
    internal uint Revision { get; private set; } = 1;
    public Resources.Terrain Terrain { get; } = terrain;
    public TerrainChunkRange Chunks { get; } = chunks;
    public int Lod { get; } = lod;
    public TransformQvvs Transform
    {
        get => _transform;
        set { _transform = value; Revision++; }
    }
    public IReadOnlyList<Material> Materials
    {
        get => _materials;
        set { _materials = value.ToArray(); Revision++; }
    }
}
