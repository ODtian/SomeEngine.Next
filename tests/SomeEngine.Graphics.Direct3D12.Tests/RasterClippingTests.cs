using System.Numerics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class RasterClippingTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void ClippingOnlyIntersectedPlanesMatchesTheSevenPlaneResult()
    {
        const int cases = 128;
        const string source = """
            #include "visbuffer_encoding.slang"
            #define CLUSTER_RASTER_TWO_SIDED 0
            #define CLUSTER_TARGET_RASTER_TWO_SIDED 0
            #include "cluster_raster_shared.slang"
            StructuredBuffer<float4> Input;
            RWStructuredBuffer<uint> Results;
            [shader("compute")]
            [numthreads(32, 1, 1)]
            void Evaluate(uint3 thread : SV_DispatchThreadID)
            {
                uint item = thread.x;
                float4 triangle[3] = { Input[item * 3u], Input[item * 3u + 1u], Input[item * 3u + 2u] };
                uint mask = ClusterRasterClipMask(triangle[0]) | ClusterRasterClipMask(triangle[1]) |
                    ClusterRasterClipMask(triangle[2]);
                float4 reduced[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
                float4 full[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
                float3 reducedBarycentrics[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
                float3 fullBarycentrics[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
                uint a = ClusterRasterClipTriangle(triangle, mask, reduced, reducedBarycentrics);
                uint b = ClusterRasterClipTriangle(triangle, 127u, full, fullBarycentrics);
                uint base = item * 130u;
                Results[base] = a;
                Results[base + 65u] = b;
                for (uint vertex = 0u; vertex < CLUSTER_RASTER_MAX_CLIPPED_VERTICES; vertex++)
                    for (uint component = 0u; component < 4u; component++)
                    {
                        Results[base + 1u + vertex * 4u + component] = vertex < a ? asuint(reduced[vertex][component]) : 0u;
                        Results[base + 66u + vertex * 4u + component] = vertex < b ? asuint(full[vertex][component]) : 0u;
                    }
            }
            """;
        var triangles = new Vector4[cases * 3];
        Vector4[] edges =
        [
            new(-.5f, -.5f, .5f, 1), new(.5f, -.5f, .5f, 1), new(0, .5f, .5f, 1),
            new(-2, -.5f, .5f, 1), new(.5f, -.5f, .5f, 1), new(0, 2, .5f, 1),
            new(-.5f, -.5f, -.5f, 1), new(.5f, -.5f, 2, 1), new(0, .5f, .5f, 1),
            new(-.5f, -.5f, .1f, -.1f), new(.5f, -.5f, .5f, 1), new(0, .5f, .5f, 1),
            new(-1, -1, 0, 1), new(1, -1, 1, 1), new(0, 1, .5f, 1),
            new(0, 0, 0, 0), new(0, 0, 0, 0), new(0, 0, 0, 0),
        ];
        edges.CopyTo(triangles, 0);
        var random = new Random(73419);
        for (int index = edges.Length; index < triangles.Length; index++)
        {
            float scale = index % 5 == 0 ? 1e-5f : 1;
            triangles[index] = new Vector4(
                (float)(random.NextDouble() * 6 - 3), (float)(random.NextDouble() * 6 - 3),
                (float)(random.NextDouble() * 4 - 1), (float)(random.NextDouble() * 2)) * scale;
        }
        uint[] input = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, uint>(triangles).ToArray();
        uint[] result = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource(source, "Evaluate", new(cases / 32, 1, 1),
            [([0u], ResourceBindingType.BufferSrv, 0), ([0u], ResourceBindingType.BufferSrv, 4),
             (input, ResourceBindingType.BufferSrv, 16), (new uint[cases * 130], ResourceBindingType.BufferUav, 4)]));
        Assert.Equal(3u, result[0]);
        Assert.Equal(0u, result[5 * 130]);
        for (int item = 0; item < cases; item++)
            Assert.Equal(result.AsSpan(item * 130 + 65, 65).ToArray(), result.AsSpan(item * 130, 65).ToArray());
    }
}
