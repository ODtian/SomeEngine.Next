using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowGeometryProjectionTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void ShadowSphereErrorUsesItsLightProjectionDepthAndAngle()
    {
        // A 90-degree projection at 512 pixels has a scale of 256. The native
        // sphere bound at depth ten, radius two gives 8*sqrt(96)/10.
        // Multiplying the error 1/64 by 256 and dividing by that bound gives
        // 0.51031036 pixels, independently of the primary camera.
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "cluster_view.slang"
            struct ProjectionData { float4x4 Matrix; };
            ConstantBuffer<ProjectionData> Projection;
            RWStructuredBuffer<float> ErrorPixels;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void MeasureShadowGeometry()
            {
                ClusterViewUniforms view = {};
                view.View = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1);
                view.GeometryViewProj = Projection.Matrix;
                view.ScreenWidth = 512u;
                view.ScreenHeight = 512u;
                view.NearPlane = 0.1;
                view.GeometryLodScale = 1.0;
                view.TargetPolicy = 1u;
                ErrorPixels[0] = ClusterSphereError(float3(0,0,-10), 2.0, 1.0/64.0, view);
            }
            """, "MeasureShadowGeometry", new(1, 1, 1),
            [(MemoryMarshal.Cast<Matrix4x4, uint>(new[] { projection }.AsSpan()).ToArray(),
                ResourceBindingType.ConstantBuffer, 0), (new uint[1], ResourceBindingType.BufferUav, 4)]);
        Assert.InRange(BitConverter.UInt32BitsToSingle(result[0][0]), .510309f, .510312f);
    }
}
