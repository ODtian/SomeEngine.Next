using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class EmissiveLightSamplingTests(D3D12TestCompiler compiler)
{
    [Fact]
    public async Task MaterialEmissionAndImportanceSamplingProduceSymmetricDirectLighting()
    {
        Vector3[] positions = [new(-1.5f, -.5f, 2), new(-1, .5f, 2), new(-.5f, -.5f, 2),
            new(.5f, -.5f, 2), new(1, .5f, 2), new(1.5f, -.5f, 2)];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, [], [0, 1, 2, 3, 4, 5],
            [new MeshMaterialSlot(Guid.Empty)], "Symmetric emitting surfaces");
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), attributes, asset.Name);
        await geometry.LoadPagesAsync(mesh, attributes, Enumerable.Range(0, mesh.PageCount));
        using MeshDistanceFieldBlock block = MeshDistanceFieldBlock.Build(geometry, attributes, 4, 32);
        MeshDistanceField field = await block.ReadDataAsync();
        byte[] fieldBytes = new byte[field.GpuByteLength];
        field.WriteGpuData(fieldBytes, mesh, 0);
        uint pointerOffset = MemoryMarshal.Read<uint>(fieldBytes.AsSpan(172));
        ReadOnlySpan<uint> pointers = MemoryMarshal.Cast<byte, uint>(fieldBytes.AsSpan((int)pointerOffset, 24));
        uint[] candidates = new uint[16];
        for (int triangle = 0; triangle < 2; triangle++)
            pointers.Slice(triangle * 3, 3).CopyTo(candidates.AsSpan(triangle * 8 + 4, 3));
        uint[] pageOffsets = new uint[mesh.PageCount];
        var pageBytes = new List<byte>();
        for (int page = 0; page < mesh.PageCount; page++)
        {
            pageOffsets[page] = (uint)pageBytes.Count;
            Assert.True(mesh.TryGetPage(page, out ReadOnlyMemory<byte> bytes));
            pageBytes.AddRange(bytes.ToArray());
            while ((pageBytes.Count & 511) != 0) pageBytes.Add(0);
        }
        byte[] hierarchyBytes = mesh.Bvh.ToArray();
        Span<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(hierarchyBytes.AsSpan());
        for (int node = 0; node < nodes.Length; node++)
        for (int child = 0; child < ClusterBVHNode.ChildCount; child++)
        {
            ClusterHierarchySlice part = nodes[node].GetChild(child);
            if (!part.IsEnabled || !part.IsLeaf) continue;
            part.ChildReference = ((pageOffsets[part.ChildReference >> 8] >> 9) << 8) | part.FirstCluster;
            nodes[node].SetChild(child, part);
        }
        const int sampleCount = 4096;
        const string source = """
            #define CLUSTER_VERTEX_ENCODING "emission-test"
            #include "triangle_surface.slang"
            #include "material_interfaces.slang"
            #include "gi_emissive_sampling.slang"
            ByteAddressBuffer PageHeap;
            typedef StaticVertexEval VertexEvaluate;
            typedef LinearSurface DomainEvaluate;
            typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
            struct EmittingSurface : IGiSurfaceEvaluate<SurfaceEval, Triangle>
            {
                float3 getGiEmissionBound(uint materialIndex) { return float3(4, 4, 0); }
                GiMaterialSurface evaluateGiSurface(GiMaterialInput input, SurfaceEval surface,
                    float3 barycentric, Triangle triangle)
                {
                    GiMaterialSurface result = (GiMaterialSurface)0;
                    result.Normal = float3(0, 0, -1);
                    result.GeometricNormal = result.Normal;
                    result.Coverage = 1.0;
                    result.Occlusion = 1.0;
                    result.Emission = surface.Position.x < 0.0 ? float3(4, 0, 0) : float3(0, 4, 0);
                    return result;
                }
            };
            typedef EmittingSurface SurfaceEvaluate;
            #include "gi_material_entries.slang"
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void EvaluateDirectLight(uint3 thread : SV_DispatchThreadID)
            {
                uint index = thread.x;
                if (index >= 4096u) return;
                GiDirectLightProposal proposal = SelectGiEmittingObject(0.0, float3(0, 0, 1), (float(index) + 0.5) / 4096.0);
                GiEmitterPointSample sample = SampleGiEmitterTriangle(0.0, float3(0, 0, 1), proposal.Instance,
                    frac((float(index) + 0.5) * float3(0.7548776662, 0.5698402910, 0.438579021)));
                GiEmissiveTriangle light = GiEmissiveTriangles[sample.Emitter];
                float3 point = light.PositionArea.xyz + light.EdgeUFlux.xyz * sample.Barycentric.y + light.EdgeV.xyz * sample.Barycentric.z;
                float3 plane = normalize(cross(light.EdgeUFlux.xyz, light.EdgeV.xyz));
                float scale = 1.0 / (proposal.Probability * sample.AreaPdf);
                Results[index * 2u] = float4(EvaluateGiEmissiveIrradiance(light.Emission.rgb, 1.0,
                    float3(0, 0, 1), plane, point, scale), 1.0);
                Results[index * 2u + 1u] = float4(EvaluateGiEmissiveIrradiance(light.Emission.rgb, 1.0,
                    float3(0, 0, -1), plane, point, scale), 1.0);
            }
            """;
        float[] transform = [0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1, 0];
        uint[][] output = new D3D12TestCompute(compiler).ExecuteSource(source,
            [("ClassifyGiMaterialEmission", new(1, 1, 1)), ("UpdateGiEmissiveTriangles", new(1, 1, 1)),
             ("BuildGiEmitterTriangleTrees", new(1, 1, 1)), ("BuildGiEmitterObjectTree", new(1, 1, 1)),
             ("EvaluateDirectLight", new(64, 1, 1))],
            new D3D12TestCompute.BufferInput[]
            {new("GiEmissive", [1, 1, 1, 0, 2, 1, 1, 0, 0, 1, 0, 0], ResourceBindingType.ConstantBuffer, 0),
             new("GiEmissionBounds", new uint[8], ResourceBindingType.BufferUav, 16),
             new("GiEmissiveTriangles", new uint[56], ResourceBindingType.BufferUav, 112),
             new("GiEmitterState", [2, 1, 0, 0, 1, 0, 0, 0], ResourceBindingType.BufferUav, 16),
             new("GiEmitterChanges", [1, 0], ResourceBindingType.BufferUav, 4),
             new("GiEmitterInstanceRanges", [0, 2], ResourceBindingType.BufferUav, 8),
             new("GiEmitterTreeRanges", [0, 2], ResourceBindingType.BufferUav, 8),
             new("GiEmitterInstanceChanges", [1], ResourceBindingType.BufferUav, 4),
             new("GiEmitterTriangleTree", new uint[32], ResourceBindingType.BufferUav, 32),
             new("GiEmitterObjectTree", new uint[16], ResourceBindingType.BufferUav, 32),
             new("GiMaterialWork", new uint[4], ResourceBindingType.ConstantBuffer, 0),
             new("GiMaterialWorkRanges", [0, 2], ResourceBindingType.BufferUav, 8),
             new("GiMaterialWorkIndices", [0, 1], ResourceBindingType.BufferUav, 4),
             new("GiCandidates", candidates, ResourceBindingType.BufferUav, 32),
             new("GiHierarchy", MemoryMarshal.Cast<byte, uint>(hierarchyBytes).ToArray(), ResourceBindingType.BufferSrv, 0),
             new("PageHeap", MemoryMarshal.Cast<byte, uint>(pageBytes.ToArray()).ToArray(), ResourceBindingType.BufferSrv, 0),
             new("InstanceData", MemoryMarshal.Cast<float, uint>(transform).ToArray(), ResourceBindingType.BufferSrv, 0),
             new("InstanceProperties", new uint[12], ResourceBindingType.ConstantBuffer, 0),
             new("Results", new uint[sampleCount * 8], ResourceBindingType.BufferUav, 16)});
        ReadOnlySpan<float> values = MemoryMarshal.Cast<uint, float>(output[^1]);
        Vector3 front = default, back = default;
        for (int index = 0; index < sampleCount; index++)
        {
            front += new Vector3(values[index * 8], values[index * 8 + 1], values[index * 8 + 2]);
            back += new Vector3(values[index * 8 + 4], values[index * 8 + 5], values[index * 8 + 6]);
        }
        front /= sampleCount;
        back /= sampleCount;
        Assert.True(front.X > .01f);
        Assert.InRange(front.Y / front.X, .95f, 1.05f);
        Assert.InRange(front.Z, 0, 1e-6f);
        Assert.InRange(back.Length(), 0, 1e-6f);
    }
}
