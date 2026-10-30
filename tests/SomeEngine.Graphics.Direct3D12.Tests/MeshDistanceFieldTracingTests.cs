using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using SomeEngine.Geometry.Cluster;
using ValueType = SomeEngine.Resources.Geometry.ValueType;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class MeshDistanceFieldTracingTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async System.Threading.Tasks.Task DistanceFieldAndCompleteGridFindOriginalMeshSurfacesByMaterial(int divisions)
    {
        List<Vector3> positions = [];
        List<uint> indices = [];
        List<float> materials = [];
        for (int plane = 0; plane < 2; plane++)
        for (int part = 0; part < divisions; part++)
        {
            float z = plane == 0 ? -1 : 1;
            uint first = (uint)positions.Count;
            positions.AddRange([new(-1 + 2f * part / divisions, -1, z),
                new(-1 + 2f * (part + 1) / divisions, -1, z), new(0, 1, z)]);
            indices.AddRange([first, first + 1, first + 2]);
            materials.AddRange([plane, plane, plane]);
        }
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions.ToArray(),
            [new RawAttribute("_MATERIAL_INDEX", materials.ToArray(), 1, ValueType.UInt8, 1, false)], indices.ToArray(),
            [new MeshMaterialSlot(Guid.Empty), new MeshMaterialSlot(Guid.Empty)], "GI query surfaces");
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        using MeshDistanceFieldBlock block = MeshDistanceFieldBlock.Build(geometry, attributes, 4, 32);
        using MeshPayloadSource payload = await geometry.OpenPayloadSourceAsync(attributes);
        MeshDistanceField field = await block.ReadDataAsync(payload);
        SomeEngine.Resources.Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, asset.Name);
        await geometry.LoadPagesAsync(mesh, attributes, Enumerable.Range(0, mesh.PageCount));
        const uint firstNode = 3;
        const int fieldAddress = 64;
        uint[] fieldWords = new uint[(field.GpuByteLength + fieldAddress) / 4];
        field.WriteGpuData(MemoryMarshal.AsBytes(fieldWords.AsSpan())[fieldAddress..], mesh, firstNode);
        uint[] pageOffsets = new uint[mesh.PageCount];
        var pageBytes = new List<byte>();
        for (int index = 0; index < mesh.PageCount; index++)
        {
            pageOffsets[index] = (uint)pageBytes.Count;
            Assert.True(mesh.TryGetPage(index, out ReadOnlyMemory<byte> page));
            pageBytes.AddRange(page.ToArray());
            while ((pageBytes.Count & 511) != 0) pageBytes.Add(0);
        }
        uint[] pages = MemoryMarshal.Cast<byte, uint>(pageBytes.ToArray().AsSpan()).ToArray();
        byte[] hierarchyBytes = new byte[firstNode * ClusterBVHNode.SizeInBytes + mesh.Bvh.Length];
        mesh.Bvh.Span.CopyTo(hierarchyBytes.AsSpan((int)firstNode * ClusterBVHNode.SizeInBytes));
        Span<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(
            hierarchyBytes.AsSpan((int)firstNode * ClusterBVHNode.SizeInBytes));
        for (int node = 0; node < nodes.Length; node++)
        for (int child = 0; child < ClusterBVHNode.ChildCount; child++)
        {
            ClusterHierarchySlice part = nodes[node].GetChild(child);
            if (!part.IsEnabled) continue;
            part.ChildReference = part.IsLeaf
                ? ((pageOffsets[part.ChildReference >> 8] >> 9) << 8) | part.FirstCluster
                : part.ChildReference + firstNode;
            nodes[node].SetChild(child, part);
        }
        uint[] hierarchy = MemoryMarshal.Cast<byte, uint>(hierarchyBytes.AsSpan()).ToArray();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f);
        Matrix4x4 transform = Matrix4x4.CreateScale(3, 1.5f, 1) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(4, -3, 5);
        Vector3 lower = new(float.MaxValue), upper = new(float.MinValue);
        foreach (Vector3 position in positions)
        {
            Vector3 world = Vector3.Transform(position, transform);
            lower = Vector3.Min(lower, world);
            upper = Vector3.Max(upper, world);
        }
        float[] bounds = [lower.X, lower.Y, lower.Z, 0, upper.X, upper.Y, upper.Z, 0,
            0, 0, 0, 0, 0, 0, 0, 0];
        float[] groups = [lower.X, lower.Y, lower.Z, 1, upper.X, upper.Y, upper.Z, 0];
        const string source = """
            #include "gi_mesh_distance_field.slang"
            #include "gi_mesh_triangles.slang"
            #include "gi_scene_tracing.slang"
            #include "vertex_evaluate.slang"
            #include "cluster_common.slang"
            ByteAddressBuffer Fields;
            ByteAddressBuffer Pages;
            ByteAddressBuffer Hierarchy;
            StructuredBuffer<float4> InstanceBounds;
            StructuredBuffer<float4> GroupBounds;
            StructuredBuffer<uint> InstanceOrder;
            RWStructuredBuffer<float4> Results;
            struct MeshTriangles : IGiMeshTriangles
            {
                RenderInstanceTransform Transform;
                float3 WorldOrigin;
                float3 WorldDirection;
                uint Material;
                bool intersect(uint3 triangle, float3 origin, float3 direction, float start, float limit,
                    out float distance, out float3 barycentric)
                {
                    uint reference = Hierarchy.Load(triangle.x);
                    uint page = (reference >> 8u) << 9u;
                    distance = limit; barycentric = 0.0;
                    if (GetClusterLocalMaterial(Pages, page, triangle.y, triangle.z) != Material) return false;
                    VertexEvalContext context = { Pages, 0u, 0u, 0u, 0u, 0u, 0u, 0u, Transform, 0u };
                    StaticPositionEval vertex;
                    return IntersectGiMeshTriangle(vertex, context, page, triangle.y, triangle.z,
                        WorldOrigin, WorldDirection, start, limit, distance, barycentric);
                }
            };
            struct SceneMeshes : IGiSceneMeshes
            {
                MeshTriangles Triangles;
                GiMeshHit traceDistanceField(uint instance, float3 origin, float3 direction, float start, float limit)
                {
                    return TraceGiMeshDistanceField(Fields, 64u, Triangles,
                        InverseTransformInstancePosition(origin, Triangles.Transform),
                        InverseTransformInstanceVector(direction, Triangles.Transform), start, limit, Triangles.Material);
                }
            };
            [shader("compute")]
            [numthreads(8, 1, 1)]
            void Evaluate(uint lane : SV_GroupIndex)
            {
                if (lane >= 6u) return;
                MeshTriangles triangles;
                triangles.Material = lane / 3u;
                uint ray = lane % 3u;
                float3 origin = ray == 0u ? float3(0.001, 0.98, 3.0) :
                    ray == 1u ? float3(0.001, 0.98, 0.5) : float3(2.0, 2.0, 3.0);
                float3 direction = float3(0, 0, -1);
                triangles.Transform = (RenderInstanceTransform)0;
                triangles.Transform.Position = float3(4, -3, 5);
                triangles.Transform.Scale = 2.0;
                triangles.Transform.Stretch = float3(1.5, 0.75, 0.5);
                triangles.Transform.Rotation = float4(0, sin(0.4), 0, cos(0.4));
                triangles.WorldOrigin = TransformInstanceLocalPosition(origin, triangles.Transform);
                triangles.WorldDirection = TransformInstanceLocalPosition(direction, triangles.Transform) - triangles.Transform.Position;
                GiMeshHit near = MissGiMesh(10.0);
                if (GiMeshMaterialIntersects(Fields, 64u, triangles.Material, origin, direction, 0.0, 10.0))
                    near = TraceGiMeshDistanceField(Fields, 64u, triangles, origin, direction, 0.0, 10.0, triangles.Material);
                GiMeshHit complete = TraceGiMeshCells(Fields, 64u, triangles, origin, direction, 0.0, 10.0);
                SceneMeshes meshes;
                meshes.Triangles = triangles;
                GiSceneHit sceneNear = TraceGiNearScene(meshes, InstanceBounds, GroupBounds, InstanceOrder,
                    1u, true, triangles.WorldOrigin, triangles.WorldDirection, 0.0, 10.0);
                Results[lane * 3u] = float4(near.Distance, float(near.Hit), near.Barycentric.xy);
                Results[lane * 3u + 1u] = float4(complete.Distance, float(complete.Hit), complete.Barycentric.xy);
                Results[lane * 3u + 2u] = float4(sceneNear.Mesh.Distance, float(sceneNear.Mesh.Hit), sceneNear.Mesh.Barycentric.xy);
            }
            """;
        uint[] words = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource(source,
            [("Evaluate", new DispatchArguments(1, 1, 1))],
            new D3D12TestCompute.BufferInput[]
            {
                new("Fields", fieldWords, ResourceBindingType.BufferSrv, 0),
                new("Pages", pages, ResourceBindingType.BufferSrv, 0),
                new("Hierarchy", hierarchy, ResourceBindingType.BufferSrv, 0),
                new("InstanceBounds", MemoryMarshal.Cast<float, uint>(bounds.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 16),
                new("GroupBounds", MemoryMarshal.Cast<float, uint>(groups.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 16),
                new("InstanceOrder", [0], ResourceBindingType.BufferSrv, 4),
                new("Results", new uint[72], ResourceBindingType.BufferUav, 16)
            }));
        float[] results = MemoryMarshal.Cast<uint, float>(words.AsSpan()).ToArray();
        Assert.InRange(results[0], 3.9999f, 4.0001f);
        Assert.Equal(1f, results[1]);
        Assert.InRange(results[12], 1.4999f, 1.5001f);
        Assert.Equal(1f, results[13]);
        Assert.Equal(0f, results[25]);
        Assert.InRange(results[36], 1.9999f, 2.0001f);
        Assert.Equal(1f, results[37]);
        Assert.Equal(0f, results[49]);
        Assert.Equal(0f, results[61]);
        for (int ray = 0; ray < 6; ray++)
        {
            int first = ray * 12;
            foreach (int query in new[] { 4, 8 })
            {
                Assert.Equal(results[first + 1], results[first + query + 1]);
                Assert.InRange(Math.Abs(results[first] - results[first + query]), 0f, 1e-4f);
                Assert.InRange(Math.Abs(results[first + 2] - results[first + query + 2]), 0f, 1e-4f);
                Assert.InRange(Math.Abs(results[first + 3] - results[first + query + 3]), 0f, 1e-4f);
            }
        }
    }
}
