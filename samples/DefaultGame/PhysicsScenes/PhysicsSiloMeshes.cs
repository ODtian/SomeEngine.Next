using System.Numerics;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;

namespace DefaultGame;

public sealed partial class PhysicsSceneExample
{
    private static Mesh CreateSiloSphere()
    {
        const int segments = 16, rings = 8, stride = segments + 1;
        var positions = new Vector3[(rings + 1) * stride];
        var attributes = new StandardClusterAttributes[positions.Length];
        var indices = new List<uint>(rings * segments * 6);
        for (int ring = 0; ring <= rings; ring++)
        for (int segment = 0; segment <= segments; segment++)
        {
            float latitude = MathF.PI * ring / rings, longitude = MathF.Tau * segment / segments;
            Vector3 normal = new(MathF.Sin(latitude) * MathF.Cos(longitude),
                MathF.Sin(latitude) * MathF.Sin(longitude), MathF.Cos(latitude));
            int index = ring * stride + segment;
            positions[index] = normal * .5f;
            attributes[index] = new(normal, new(-MathF.Sin(longitude), MathF.Cos(longitude), 0, 1),
                new(segment / (float)segments, ring / (float)rings));
            if (ring < rings && segment < segments)
            {
                uint first = (uint)index, next = first + stride;
                indices.AddRange([first, next, first + 1, first + 1, next, next + 1]);
            }
        }
        return ClusterMeshBuilder.Build(positions, attributes, indices.ToArray(),
            [new(0, indices.Count, 0)], StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Silo sphere");
    }

    private static Mesh CreateSiloHull()
    {
        var hull = PhysicsSiloScenes.Hull;
        var positions = new Vector3[hull.FaceVertices.Length];
        var attributes = new StandardClusterAttributes[positions.Length];
        var indices = new uint[positions.Length];
        for (int face = 0; face < hull.Faces.Length; face++)
        {
            var polygon = hull.Faces.Span[face];
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, polygon.Normal));
            for (int vertex = 0; vertex < polygon.IndexCount; vertex++)
            {
                int index = polygon.FirstIndex + vertex;
                positions[index] = hull.Vertices.Span[hull.FaceVertices.Span[index]] / (PhysicsSiloScenes.Radius * 2);
                attributes[index] = new(polygon.Normal, new(tangent, 1), Vector2.Zero);
                indices[index] = (uint)index;
            }
        }
        return ClusterMeshBuilder.Build(positions, attributes, indices,
            [new(0, indices.Length, 0)], StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Silo hull");
    }
}
