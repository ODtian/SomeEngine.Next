using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Builds persistent terrain geometry through the full CPU Cluster simplification and LOD pipeline.</summary>
public static class TerrainClusterCache
{
    public static async ValueTask<TerrainClusterBlock> BuildAsync<TAttributes>(TerrainCpuData source,
        TerrainEditing editing, IReadOnlyList<TerrainChunkBounds> chunks, ITerrainAttributeGenerator<TAttributes> attributes,
        IClusterVertexEncoder<TAttributes> encoder, TerrainGeometryIdentity identity, ITerrainMesher? mesher = null)
        where TAttributes : unmanaged
    {
        mesher ??= new MarchingCubesTerrainMesher();
        var adapter = new TerrainClusterAdapter<TAttributes>(attributes, encoder, ClusterBuildStrategy.Offline);
        TerrainFieldInputs fields = adapter.Fields;
        int mesherBorder = mesher.ReadBorder, attributeBorder = adapter.ReadBorder;
        string encoding = encoder.Compatibility;
        TerrainChunkBounds[] bounds = chunks.ToArray();
        var meshes = new Mesh?[bounds.Length];
        using Resources.Terrain terrain = source.CreateTerrain(editing);
        if (terrain.Surface is { } surface && bounds.Length > 0)
        {
            int border = mesherBorder + attributeBorder;
            TerrainBounds volume = bounds[0].Volume.Expand(border * bounds[0].CellSize);
            foreach (TerrainChunkBounds chunk in bounds.AsSpan(1))
                volume = volume.Union(chunk.Volume.Expand(border * chunk.CellSize));
            TerrainSurfaceJob work = surface.ScheduleRead(volume, new CacheWork(bounds, meshes, mesher, adapter), bounds.Length);
            await work.WaitAsync().ConfigureAwait(false);
        }
        var result = new TerrainClusterBlock
        {
            Identity = identity, VertexEncoding = encoding,
            MesherReadBorder = mesherBorder, AttributeReadBorder = attributeBorder,
        };
        result.SetSource(source, fields);
        for (int index = 0; index < bounds.Length; index++)
        {
            TerrainChunkBounds chunk = bounds[index];
            result.AddGeometry(new(chunk.Coordinates.X, chunk.Coordinates.Y, chunk.Coordinates.Z, chunk.CellCount,
                chunk.CellSize, chunk.Lod, (byte)chunk.TransitionFaces, source.Revision), meshes[index]);
        }
        return result;
    }

    private readonly struct CacheWork(TerrainChunkBounds[] chunks, Mesh?[] meshes,
        ITerrainMesher mesher, ITerrainClusterAdapter adapter) : ITerrainSurfaceWork
    {
        public void Execute(TerrainSurfaceRead surface, int index)
        {
            TerrainChunkBounds bounds = chunks[index];
            meshes[index] = adapter.Build(surface, mesher.Build(surface, bounds), $"Terrain chunk {bounds.Coordinates}");
        }
    }
}
