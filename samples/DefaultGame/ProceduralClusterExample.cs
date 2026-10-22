using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>An example of two adjacent quads with explicit vertex attributes and material groups.</summary>
public static class ProceduralClusterExample
{
    public static Mesh CreateMesh(ClusterBuildStrategy strategy = ClusterBuildStrategy.Realtime)
    {
        // These dimensions, UVs and material regions are example scene data.
        Vector3[] positions =
        [
            new(-2, -1, 0), new(0, -1, 0), new(-2, 1, 0),
            new(0, 1, 0), new(2, -1, 0), new(2, 1, 0),
        ];
        StandardClusterAttributes[] attributes = positions.Select(position =>
            new StandardClusterAttributes(-Vector3.UnitZ, new(1, 0, 0, 1),
                new((position.X + 2) / 4, (position.Y + 1) / 2))).ToArray();
        uint[] indices = [0, 2, 1, 1, 2, 3, 1, 3, 4, 4, 3, 5];
        ClusterMaterialGroup[] materialGroups = [new(0, 6, 0), new(6, 6, 1)];
        return ClusterMeshBuilder.Build(positions, attributes, indices, materialGroups,
            StandardClusterVertexEncoder.Instance, strategy, "Two procedural quads");
    }

    public static Entity Add(SceneEdit scene, Material firstMaterial, Material secondMaterial)
        => scene.AddEntity(CreateMesh(), [firstMaterial, secondMaterial],
            new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity));
}
