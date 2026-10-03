using System.Numerics;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

internal static class RasterTestGeometry
{
    internal static async Task<Mesh> CreateDeepAsync()
    {
        const int extraLevels = 4;
        Mesh mesh = await CreateAsync();
        var original = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, SomeEngine.Resources.Geometry.ClusterBVHNode>(mesh.Bvh.Span).ToArray();
        var nodes = new SomeEngine.Resources.Geometry.ClusterBVHNode[original.Length + extraLevels];
        original.CopyTo(nodes, 0);
        for (int level = 0; level < extraLevels; level++)
            nodes[original.Length + level] = new SomeEngine.Resources.Geometry.ClusterBVHNode
            {
                BoundMin = original[^1].BoundMin, BoundMax = original[^1].BoundMax,
                LODSphere = original[^1].LODSphere, LODError = original[^1].LODError,
                ChildPointer = checked((uint)(original.Length + level - 1)), ChildCount = 1, NodeType = 0,
            };
        var pages = Enumerable.Range(0, mesh.PageCount).Select(index =>
        {
            Assert.True(mesh.TryGetPage(index, out var bytes));
            return bytes;
        }).ToArray();
        return Mesh.Create("Deep octahedron", mesh.VertexEncoding, mesh.PageDigests,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray(), pages,
            mesh.Bounds, mesh.Regions, mesh.LowestDetailPageIndices);
    }

    internal static async Task<Mesh> CreateAsync()
    {
        Vector3[] positions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ];
        uint[] triangles = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
            2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        float[] normals = positions.SelectMany(static value => new[] { value.X, value.Y, value.Z }).ToArray();
        List<RawAttribute> attributes =
        [
            new("NORMAL", normals, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[positions.Length * 2], 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, triangles,
            [new MeshMaterialSlot(Guid.Empty)], "Raster test octahedron");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        return mesh;
    }
}
