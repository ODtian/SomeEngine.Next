using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class GiMaterialLightingTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void DiffuseRadianceStaysTheSameWhenViewDirectionChanges()
    {
        const string source = """
            #include "brdf.slang"
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(3, 1, 1)]
            void Evaluate(uint lane : SV_GroupIndex)
            {
                float3 view = lane == 0u ? float3(0, 0, 1) :
                    lane == 1u ? normalize(float3(1, 0, 0.1)) : float3(0, 0, -1);
                float3 diffuse;
                float3 color = EvaluateDirectionalLight(float3(0.8, 0.4, 0.2), 0.0, 0.4,
                    float3(0, 0, 1), view, float3(0, 0, 1), float3(4, 3, 2), 0.5, diffuse);
                Results[lane] = float4(diffuse, dot(color, 1.0.xxx));
            }
            """;
        uint[] words = Assert.Single(new D3D12TestCompute(compiler).ExecuteSource(source,
            "Evaluate", new(1, 1, 1), [(new uint[12], ResourceBindingType.BufferUav, 16)]));
        ReadOnlySpan<float> values = MemoryMarshal.Cast<uint, float>(words);
        Assert.True(values[0] > 0.0f);
        for (int component = 0; component < 3; component++)
        {
            Assert.InRange(values[4 + component], values[component] - 1e-6f, values[component] + 1e-6f);
            Assert.InRange(values[8 + component], values[component] - 1e-6f, values[component] + 1e-6f);
        }
        Assert.True(values[3] > values[11]);
    }
}
