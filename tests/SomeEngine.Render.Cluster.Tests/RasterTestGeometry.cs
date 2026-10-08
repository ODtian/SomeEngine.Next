using System.Numerics;
using System.Collections.Immutable;
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
        original.CopyTo(nodes, extraLevels);
        for (int index = extraLevels; index < nodes.Length; index++)
            for (int child = 0; child < 4; child++)
            {
                var slice = nodes[index].GetChild(child);
                if (slice.IsEnabled && !slice.IsLeaf) { slice.ChildReference += extraLevels; nodes[index].SetChild(child, slice); }
            }
        for (int level = 0; level < extraLevels; level++)
        {
            var slice = original[0].GetChild(0);
            slice.PageRangeKey = uint.MaxValue;
            slice.ChildReference = (uint)(level + 1);
            slice.GroupPartData = (511u << 16) | 0xFFFFu;
            nodes[level].SetChild(0, slice);
        }
        var pages = Enumerable.Range(0, mesh.PageCount).Select(index =>
        {
            Assert.True(mesh.TryGetPage(index, out var bytes));
            return bytes;
        }).ToArray();
        return Mesh.Create("Deep octahedron", mesh.VertexEncoding, mesh.PageDigests,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray(), pages,
            mesh.Bounds, mesh.Regions, mesh.LowestDetailPageIndices,
            mesh.ClusterGroups.Select(group => group with
            {
                HierarchyParts = group.HierarchyParts.Select(part => part with { NodeIndex = part.NodeIndex + extraLevels }).ToImmutableArray(),
            }).ToArray());
    }

    internal static async Task<Mesh> CreatePlaneAsync()
    {
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        List<RawAttribute> attributes =
        [
            new("NORMAL", new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 }, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, [0, 1, 2, 0, 2, 3],
            [new MeshMaterialSlot(Guid.Empty)], "Authored plane");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        return mesh;
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
