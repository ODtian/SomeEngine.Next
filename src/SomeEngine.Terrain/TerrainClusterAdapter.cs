using System.Numerics;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

public interface ITerrainAttributeGenerator<TAttributes> where TAttributes : unmanaged
{
    int ReadBorder { get; }
    TAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point);
    byte GetMaterial(TerrainSurfaceRead surface, Vector3 position);
}

public interface ITerrainClusterAdapter
{
    int ReadBorder { get; }
    Mesh? Build(TerrainSurfaceRead surface, TerrainSurfaceMesh geometry, string name);
}

/// <summary>Connects a user's terrain attributes and matching encoding to the common CPU Cluster builder.</summary>
public sealed class TerrainClusterAdapter<TAttributes>(ITerrainAttributeGenerator<TAttributes> attributes,
    IClusterVertexEncoder<TAttributes> encoder, ClusterBuildStrategy strategy) : ITerrainClusterAdapter
    where TAttributes : unmanaged
{
    public int ReadBorder => attributes.ReadBorder;

    public Mesh? Build(TerrainSurfaceRead surface, TerrainSurfaceMesh geometry, string name)
    {
        if (geometry.Indices.IsEmpty) return null;
        var values = new TAttributes[geometry.Positions.Length];
        ReadOnlySpan<TerrainSurfacePoint> samples = geometry.Samples.Span;
        for (int vertex = 0; vertex < values.Length; vertex++)
            values[vertex] = attributes.CreateVertex(surface, samples[vertex]);

        var groups = new List<ClusterMaterialGroup>();
        ReadOnlySpan<Vector3> positions = geometry.Positions.Span;
        ReadOnlySpan<uint> indices = geometry.Indices.Span;
        int start = 0;
        byte material = 0;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            Vector3 centre = (positions[(int)indices[triangle]] + positions[(int)indices[triangle + 1]]
                + positions[(int)indices[triangle + 2]]) / 3f;
            byte nextMaterial = attributes.GetMaterial(surface, centre);
            if (triangle > start && nextMaterial != material)
            {
                groups.Add(new(start, triangle - start, material));
                start = triangle;
            }
            material = nextMaterial;
        }
        groups.Add(new(start, indices.Length - start, material));
        return ClusterMeshBuilder.Build(geometry.PositionArray, values, geometry.IndexArray, groups,
            encoder, strategy, name);
    }
}
