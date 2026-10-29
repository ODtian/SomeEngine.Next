using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class TriangleVertexEvaluationTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(0u, 2f)]
    [InlineData(1u, 5f)]
    public void TriangleVertexEvaluationPreservesUserDataForEachFrame(uint previousFrame, float translation)
    {
        Vector4[] source =
        [
            new(1, 2, 3, 11), new(4, 5, 6, 22), new(7, 8, 9, 33),
            new(.25f, .5f, 0, 0), new(.5f, .75f, 0, 0), new(.75f, 1, 0, 0),
        ];
        uint[] result = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "generated/render_instance_layout.slang"
            #include "vertex_consumer_cache.slang"
            ByteAddressBuffer Values;
            struct FrameData { uint PreviousFrame; };
            ConstantBuffer<FrameData> Frame;
            RWStructuredBuffer<float4> Output;
            struct UserVertex
            {
                float3 Position;
                float4 Tint;
                float2 Coordinates;
            };
            struct UserVertexEvaluate : IVertexEvaluate
            {
                typealias Vertex = UserVertex;
                Vertex evaluate(VertexEvalContext context)
                {
                    float4 value = asfloat(context.pageHeap.Load4(context.vertexBlobAddress + context.localVertexIndex * 16u));
                    Vertex result;
                    result.Position = TransformInstanceLocalPosition(value.xyz, context.transform);
                    result.Tint = float4(value.w, context.localVertexIndex, context.IsPreviousFrame, context.instanceID);
                    result.Coordinates = asfloat(context.pageHeap.Load2(context.attributeBlobAddress + context.localVertexIndex * 16u));
                    return result;
                }
                float3 getPosition(Vertex vertex) { return vertex.Position; }
                Vertex readLane(Vertex vertex, uint lane)
                {
                    vertex.Position = WaveReadLaneAt(vertex.Position, lane);
                    vertex.Tint = WaveReadLaneAt(vertex.Tint, lane);
                    vertex.Coordinates = WaveReadLaneAt(vertex.Coordinates, lane);
                    return vertex;
                }
            };
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void EvaluateUserTriangle()
            {
                VertexEvalContext context;
                context.pageHeap = Values;
                context.vertexBlobAddress = 0u;
                context.vertexBlobSize = 48u;
                context.attributeBlobAddress = 48u;
                context.attributeBlobSize = 48u;
                context.localVertexIndex = 0u;
                context.visibleClusterIndex = 0u;
                context.instanceID = 13u;
                context.IsPreviousFrame = Frame.PreviousFrame;
                context.transform.Rotation = float4(0, 0, 0, 1);
                context.transform.Position = (Frame.PreviousFrame == 0u ? 2.0 : 5.0).xxx;
                context.transform.Scale = 2.0;
                context.transform.Stretch = 1.0.xxx;
                context.transform.Padding = 0.0;
                UserVertexEvaluate evaluator;
                TriangleVertices<UserVertex> vertices = EvaluateTriangleVertices(evaluator, context, uint3(2, 0, 1));
                Output[0] = float4(vertices.First.Position, 1);
                Output[1] = vertices.First.Tint;
                Output[2] = float4(vertices.First.Coordinates, 0, 1);
                Output[3] = float4(vertices.Second.Position, 1);
                Output[4] = vertices.Second.Tint;
                Output[5] = float4(vertices.Second.Coordinates, 0, 1);
                Output[6] = float4(vertices.Third.Position, 1);
                Output[7] = vertices.Third.Tint;
                Output[8] = float4(vertices.Third.Coordinates, 0, 1);
            }
            """, "EvaluateUserTriangle", new(1, 1, 1),
            [(MemoryMarshal.Cast<Vector4, uint>(source).ToArray(), ResourceBindingType.BufferSrv, 0),
             ([previousFrame], ResourceBindingType.ConstantBuffer, 0),
             (new uint[36], ResourceBindingType.BufferUav, 16)]));
        var expected = new List<Vector4>();
        foreach (int index in new[] { 2, 0, 1 })
        {
            Vector4 value = source[index];
            expected.Add(new Vector4(new Vector3(value.X, value.Y, value.Z) * 2 + new Vector3(translation), 1));
            expected.Add(new(value.W, index, previousFrame, 13));
            expected.Add(new(source[index + 3].X, source[index + 3].Y, 0, 1));
        }
        Assert.Equal(expected, MemoryMarshal.Cast<uint, Vector4>(result).ToArray());
    }
}
