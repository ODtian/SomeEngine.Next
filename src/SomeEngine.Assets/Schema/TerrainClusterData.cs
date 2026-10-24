using System.Numerics;

namespace SomeEngine.Assets.Schema;

public readonly record struct TerrainClusterMesh(TerrainClusterEntry Entry, Mesh? Mesh);

/// <summary>Ready cached geometry and immutable source descriptions used to select reusable chunks.</summary>
public sealed class TerrainClusterData
{
    private readonly TerrainSurfaceValues? _surface;
    private readonly TerrainHeightField[] _heightFields;
    private readonly TerrainVoxelField[] _voxelFields;
    private readonly Dictionary<(int Field, int X, int Z), (ulong Revision, Guid Id)> _heightPages;
    private readonly Dictionary<(int Field, int X, int Y, int Z), (ulong Revision, Guid Id)> _voxelPages;

    internal TerrainClusterData(TerrainClusterBlock source, TerrainClusterMesh[] meshes)
    {
        Identity = source.Identity;
        VertexEncoding = source.VertexEncoding;
        MesherReadBorder = source.MesherReadBorder;
        AttributeReadBorder = source.AttributeReadBorder;
        Fields = new(source.HeightFieldIds.ToImmutableArray(), source.VoxelFieldIds.ToImmutableArray());
        Meshes = Array.AsReadOnly(meshes);
        _surface = source.GetSurface();
        _heightFields = source.HeightFields.ToArray();
        _voxelFields = source.VoxelFields.ToArray();
        _heightPages = source.HeightPages.ToDictionary(page => (page.FieldId, page.X, page.Z), page => (page.Revision, page.Id));
        _voxelPages = source.VoxelPages.ToDictionary(page => (page.FieldId, page.X, page.Y, page.Z), page => (page.Revision, page.Id));
    }

    public TerrainGeometryIdentity Identity { get; }
    public string VertexEncoding { get; }
    public int MesherReadBorder { get; }
    public int AttributeReadBorder { get; }
    public TerrainFieldInputs Fields { get; }
    public IReadOnlyList<TerrainClusterMesh> Meshes { get; }

    /// <summary>Compares graph, grids and every sampled page version, including previously empty pages.</summary>
    public bool MatchesSources(Terrain terrain, TerrainClusterEntry entry)
    {
        float size = entry.CellCount * entry.CellSize;
        Vector3 min = new Vector3(entry.X, entry.Y, entry.Z) * size;
        TerrainBounds bounds = new TerrainBounds(min, min + new Vector3(size))
            .Expand((MesherReadBorder + AttributeReadBorder) * entry.CellSize);
        lock (terrain.Gate)
        {
            if (_surface != terrain.CaptureSurfaceValues()) return false;
            foreach (TerrainHeightField source in _heightFields)
            {
                if (source.Id >= terrain.HeightFields.Count) return false;
                HeightField field = terrain.HeightFields[source.Id];
                HeightGrid grid = field.Grid;
                if (grid != new HeightGrid(source.Width, source.Depth, source.PageSize, source.Spacing)
                    || field.InitialHeight != source.InitialHeight) return false;
                int minX = Math.Clamp((int)MathF.Floor(bounds.Min.X / grid.SampleSpacing), 0, grid.Width - 1);
                int minZ = Math.Clamp((int)MathF.Floor(bounds.Min.Z / grid.SampleSpacing), 0, grid.Depth - 1);
                int maxX = Math.Clamp((int)MathF.Floor(bounds.Max.X / grid.SampleSpacing) + 1, 0, grid.Width - 1);
                int maxZ = Math.Clamp((int)MathF.Floor(bounds.Max.Z / grid.SampleSpacing) + 1, 0, grid.Depth - 1);
                for (int z = minZ / grid.PageSize; z <= maxZ / grid.PageSize; z++)
                for (int x = minX / grid.PageSize; x <= maxX / grid.PageSize; x++)
                {
                    var previous = _heightPages.GetValueOrDefault((field.Id, x, z));
                    field.Data.Pages.TryGetValue((x, z), out TerrainFieldPage? page);
                    if (previous != (page?.Revision ?? 0, page?.Id ?? Guid.Empty)) return false;
                }
            }
            foreach (TerrainVoxelField source in _voxelFields)
            {
                if (source.Id >= terrain.VoxelFields.Count) return false;
                VoxelField field = terrain.VoxelFields[source.Id];
                VoxelGrid grid = field.Grid;
                if (grid != new VoxelGrid(source.PageSize, source.Spacing) || field.InitialValue != source.InitialValue) return false;
                int minX = grid.PageCoordinate((int)MathF.Floor(bounds.Min.X / grid.SampleSpacing));
                int minY = grid.PageCoordinate((int)MathF.Floor(bounds.Min.Y / grid.SampleSpacing));
                int minZ = grid.PageCoordinate((int)MathF.Floor(bounds.Min.Z / grid.SampleSpacing));
                int maxX = grid.PageCoordinate((int)MathF.Floor(bounds.Max.X / grid.SampleSpacing) + 1);
                int maxY = grid.PageCoordinate((int)MathF.Floor(bounds.Max.Y / grid.SampleSpacing) + 1);
                int maxZ = grid.PageCoordinate((int)MathF.Floor(bounds.Max.Z / grid.SampleSpacing) + 1);
                for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var previous = _voxelPages.GetValueOrDefault((field.Id, x, y, z));
                    field.Data.Pages.TryGetValue((x, y, z), out TerrainFieldPage? page);
                    if (previous != (page?.Revision ?? 0, page?.Id ?? Guid.Empty)) return false;
                }
            }
            return true;
        }
    }
}
