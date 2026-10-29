using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class GlobalDistanceFieldTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void DistanceFieldHitReturnsPositionOnTheRayAndSurfaceNormal()
    {
        const string source = """
            #include "gi_scene_tracing.slang"
            StructuredBuffer<float> Distances;
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Evaluate()
            {
                GiGlobalField field = (GiGlobalField)0;
                field.Lower = -2.0;
                field.Upper = 2.0;
                field.Pitch = 0.25;
                field.Shape = uint3(16, 16, 16);
                GiDistanceHit hit = TraceGiGlobalScene(field, Distances,
                    float3(0, 0, 1.5), float3(0.6, 0, -0.8), 0.0, 4.0);
                Results[0] = float4(hit.Position, float(hit.Hit));
                Results[1] = float4(hit.Normal, hit.Distance);
            }
            """;
        uint[] output = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource(source, "Evaluate", new(1, 1, 1),
            [(PlaneDistances(), ResourceBindingType.BufferSrv, 4), (new uint[8], ResourceBindingType.BufferUav, 16)]));
        ReadOnlySpan<float> values = MemoryMarshal.Cast<uint, float>(output);
        Vector3 position = new(values[0], values[1], values[2]);
        Vector3 normal = new(values[4], values[5], values[6]);
        Vector3 rayPosition = new Vector3(0, 0, 1.5f) + new Vector3(.6f, 0, -.8f) * values[7];
        Assert.Equal(1f, values[3]);
        Assert.InRange(Vector3.Distance(position, rayPosition), 0, 1e-4f);
        Assert.InRange(MathF.Abs(position.Z), 0, .125f);
        Assert.InRange(values[7], 1.75f, 2f);
        Assert.InRange(Vector3.Dot(normal, Vector3.UnitZ), .999f, 1.001f);
    }

    [Fact]
    public void DistanceFieldOccludesOnlyRaysThatReachTheSurface()
    {
        const string source = """
            #include "gi_scene_tracing.slang"
            StructuredBuffer<float> Distances;
            RWStructuredBuffer<uint> Results;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Evaluate()
            {
                GiGlobalField field = (GiGlobalField)0;
                field.Lower = -2.0;
                field.Upper = 2.0;
                field.Pitch = 0.25;
                field.Shape = uint3(16, 16, 16);
                Results[0] = TraceGiGlobalScene(field, Distances, float3(0, 0, 1.5), float3(0, 0, -1), 0.0, 2.0).Hit;
                Results[1] = TraceGiGlobalScene(field, Distances, float3(0, 0, 1.5), float3(0, 0, 1), 0.0, 2.0).Hit;
                Results[2] = TraceGiGlobalScene(field, Distances, float3(0, 0, 0.15), float3(0, 0, -1), 0.0, 0.05).Hit;
            }
            """;
        uint[] output = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource(source, "Evaluate", new(1, 1, 1),
            [(PlaneDistances(), ResourceBindingType.BufferSrv, 4), (new uint[3], ResourceBindingType.BufferUav, 4)]));
        Assert.Equal(new uint[] { 1, 0, 0 }, output);
    }

    private static uint[] PlaneDistances()
    {
        float[] distances = new float[16 * 16 * 16];
        for (int index = 0; index < distances.Length; index++)
            distances[index] = MathF.Abs(-2f + (index / (16 * 16) + .5f) * .25f);
        return MemoryMarshal.Cast<float, uint>(distances.AsSpan()).ToArray();
    }
}
