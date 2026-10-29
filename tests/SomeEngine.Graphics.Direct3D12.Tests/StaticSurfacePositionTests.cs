using System.Runtime.InteropServices;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class StaticSurfacePositionTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void PositionOnlyLinearSurfacePreservesTheFullSurfacePosition()
    {
        uint[] bytes = new uint[32];
        float[] header = [1f, -2f, 3f, .01f];
        MemoryMarshal.Cast<float, uint>(header).CopyTo(bytes);
        ushort[] coordinates = [0, 0, 0, 100, 0, 0, 0, 100, 0, 0];
        MemoryMarshal.AsBytes(coordinates.AsSpan()).CopyTo(MemoryMarshal.AsBytes(bytes.AsSpan())[16..]);
        float[] transform = [0, 0, .3f, MathF.Sqrt(.91f), 5, 2, -8, .8f, .7f, 1.3f, .9f, 0];
        var compute = new D3D12TestCompute(compiler);
        uint[][] values = compute.ExecuteSourceImages("""
            #include "triangle_surface.slang"
            #include "vertex_consumer_cache.slang"
            ByteAddressBuffer PageHeap;
            StructuredBuffer<RenderInstanceTransform> Transforms;
            RWStructuredBuffer<float3> Positions;
            [shader("compute")]
            [numthreads(32,1,1)]
            void EvaluatePositions(uint index : SV_DispatchThreadID)
            {
                VertexEvalContext context;
                context.pageHeap = PageHeap;
                context.vertexBlobAddress = 0u;
                context.vertexBlobSize = 34u;
                context.attributeBlobAddress = 64u;
                context.attributeBlobSize = 16u;
                context.localVertexIndex = 0u;
                context.visibleClusterIndex = 0u;
                context.instanceID = 0u;
                context.transform = Transforms[0];
                context.IsPreviousFrame = 0u;
                StaticVertexEval fullVertex;
                StaticSurfacePositionEval positionVertex;
                LinearSurface fullDomain;
                LinearPositionSurface positionDomain;
                TriangleVertices<StandardVertex> fullVertices = EvaluateTriangleVertices(fullVertex, context, uint3(0,1,2));
                TriangleVertices<StandardVertex> positionVertices = EvaluateTriangleVertices(positionVertex, context, uint3(0,1,2));
                Triangle fullTriangle = fullDomain.makeTriangle(fullVertices.First, fullVertices.Second, fullVertices.Third, 0u);
                Triangle positionTriangle = positionDomain.makeTriangle(positionVertices.First, positionVertices.Second, positionVertices.Third, 0u);
                fullTriangle = fullDomain.readLane(fullTriangle, WaveGetLaneIndex());
                positionTriangle = positionDomain.readLane(positionTriangle, WaveGetLaneIndex());
                SurfaceReconstructionInput input = {};
                input.ParentValue = float3(.25, float(index) / 64.0, .75 - float(index) / 64.0);
                input.TargetPosition = fullDomain.position(fullTriangle, input.ParentValue);
                float3 fullBarycentric = fullDomain.reconstruct(fullTriangle, input).Value;
                float3 positionBarycentric = positionDomain.reconstruct(positionTriangle, input).Value;
                Positions[index * 2u] = fullDomain.eval(fullTriangle, fullBarycentric).Position;
                Positions[index * 2u + 1u] = positionDomain.eval(positionTriangle, positionBarycentric).Position;
            }
            """, "EvaluatePositions", new(1,1,1),
            [new BufferInput("PageHeap", bytes, ResourceBindingType.BufferSrv, 0),
             new BufferInput("Transforms", MemoryMarshal.Cast<float,uint>(transform).ToArray(), ResourceBindingType.BufferSrv, 48),
             new BufferInput("Positions", new uint[32 * 6], ResourceBindingType.BufferUav, 12)], []);
        for (int index = 0; index < 32; index++)
            Assert.Equal(values[0].AsSpan(index * 6, 3).ToArray(), values[0].AsSpan(index * 6 + 3, 3).ToArray());
    }
}
