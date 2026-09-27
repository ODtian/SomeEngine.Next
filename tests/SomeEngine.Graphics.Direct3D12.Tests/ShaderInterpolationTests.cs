using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ShaderInterpolationTests
{
    [Theory]
    [InlineData(1.0f, false, false)]
    [InlineData(.0001220703125f, false, false)]
    [InlineData(.0001220703125f, true, false)]
    [InlineData(.0001220703125f, false, true)]
    public void PerspectiveInterpolationPreservesVertexColorsOnThinTriangles(
        float thickness, bool reverseWinding, bool perspective)
    {
        Vector4[] vertices =
        [
            new(0, 0, 1, 0), new(1024, 1024, perspective ? 2 : 1, 0),
            new(1024, 1024 + thickness * 1024, perspective ? .5f : 1, 0),
            new(768, 768 + thickness * 512, reverseWinding ? 1 : 0, 0),
        ];
        uint[] input = MemoryMarshal.Cast<Vector4, uint>(vertices).ToArray();
        uint[] result = Assert.Single(D3D12TestCompute.ExecuteSource("""
            #include "cluster_common.slang"
            StructuredBuffer<float4> Vertices;
            RWStructuredBuffer<float4> Color;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void InterpolateColor()
            {
                ScreenProjection first = { Vertices[0].xy, Vertices[0].z, 0.0 };
                ScreenProjection second = { Vertices[1].xy, Vertices[1].z, 0.0 };
                ScreenProjection third = { Vertices[2].xy, Vertices[2].z, 0.0 };
                bool reverse = Vertices[3].z != 0.0;
                float3 weight = reverse
                    ? ComputePerspectiveBarycentric(Vertices[3].xy, first, third, second)
                    : ComputePerspectiveBarycentric(Vertices[3].xy, first, second, third);
                float3 red = float3(1, 0, 0), green = float3(0, 1, 0), blue = float3(0, 0, 1);
                Color[0] = float4(weight.x * red + weight.y * (reverse ? blue : green) +
                    weight.z * (reverse ? green : blue), 1);
            }
            """, "InterpolateColor", new(1, 1, 1),
            [([0u], ResourceBindingType.BufferSrv, 0), ([0u], ResourceBindingType.ConstantBuffer, 0),
             (input, ResourceBindingType.BufferSrv, 16), (new uint[4], ResourceBindingType.BufferUav, 16)]));
        Vector4 expected = perspective ? new(.25f, .5f, .25f, 1) : new(.25f, .25f, .5f, 1);
        Vector4 color = MemoryMarshal.Cast<uint, Vector4>(result)[0];
        Assert.InRange(Vector4.Distance(expected, color), 0, 1e-5f);
    }
}
