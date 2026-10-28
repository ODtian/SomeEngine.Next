using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowReceiverInputsTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void LocalReceiverCoverageUsesTheEmitterAngleAtItsActualDistance()
    {
        var compute = new D3D12TestCompute(compiler);
        uint[][] coverage = compute.ExecuteSource("""
            #include "surface_receiver.slang"
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(2,1,1)]
            void ReadLocalReceiverCoverage(uint index : SV_DispatchThreadID)
            {
                SurfaceReceiver receiver = (SurfaceReceiver)0;
                receiver.ShadingNormal = receiver.GeometricNormal = float3(sqrt(.9375),0,-.25);
                receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
                receiver.Depth = .5;
                receiver.Position = float3(index == 0u ? -.5 : .5,0,.5);
                float3 lightPosition = float3(receiver.Position.xy,index == 0u ? .51 : 1.5);
                float worldUnitsPerCentimeter = .01;
                Coverage[index] = SurfaceReceiverReceivesLocalShadow(receiver,
                    (lightPosition - receiver.Position) / worldUnitsPerCentimeter,
                    .01 / worldUnitsPerCentimeter, true) ? 1u : 0u;
            }
            """, "ReadLocalReceiverCoverage", new(1,1,1),
            [(new uint[2], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal(new uint[] { 1,0 }, coverage[0]);
    }

    [Fact]
    public void UserReceiversKeepSubsurfaceCoverageAndRejectUnlitBackfaces()
    {
        var compute = new D3D12TestCompute(compiler);
        uint[][] coverage = compute.ExecuteSource("""
            #include "surface_receiver.slang"
            struct Surface { float3 Normal; uint Model; };
            struct Triangle { float3 Normal; };
            struct ReceiverMaterial
            {
                SurfaceReceiver evaluateReceiver(Surface surface, Triangle triangle)
                {
                    SurfaceReceiver receiver = (SurfaceReceiver)0;
                    receiver.ShadingNormal = surface.Normal;
                    receiver.GeometricNormal = triangle.Normal;
                    receiver.Flags = SURFACE_RECEIVER_PRESENT;
                    if (surface.Model != 2u) receiver.Flags |= SURFACE_RECEIVER_LIGHTING;
                    if (surface.Model == 1u) receiver.Flags |= SURFACE_RECEIVER_SUBSURFACE;
                    receiver.Depth = .5;
                    return receiver;
                }
            };
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(4,1,1)]
            void ReadReceiverCoverage(uint index : SV_DispatchThreadID)
            {
                Surface surface;
                surface.Model = index;
                surface.Normal = index < 2u ? float3(0,0,-1) : float3(0,0,1);
                Triangle triangle; triangle.Normal = surface.Normal;
                ReceiverMaterial material;
                SurfaceReceiver receiver = material.evaluateReceiver(surface, triangle);
                Coverage[index] = SurfaceReceiverReceivesDirectionalShadow(
                    receiver, float3(0,0,1), 0.0, true) ? 1u : 0u;
            }
            """, "ReadReceiverCoverage", new(1,1,1),
            [(new uint[4], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal(new uint[] { 0,1,0,1 }, coverage[0]);
    }
}
