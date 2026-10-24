using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class LightAttenuationTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void LocalLightsFadeContinuouslyBeforeTheirRangeBoundary()
    {
        const string source = """
            #include "brdf.slang"
            RWStructuredBuffer<float> Results;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Evaluate()
            {
                Results[0] = EvaluateDistanceAttenuation(0.0, 8.0);
                Results[1] = EvaluateDistanceAttenuation(16.0, 8.0);
                Results[2] = EvaluateDistanceAttenuation(49.0, 8.0);
                Results[3] = EvaluateDistanceAttenuation(63.0, 8.0);
                Results[4] = EvaluateDistanceAttenuation(63.99, 8.0);
                Results[5] = EvaluateDistanceAttenuation(64.0, 8.0);
                Results[6] = EvaluateDistanceAttenuation(65.0, 8.0);
                Results[7] = EvaluateDistanceAttenuation(0.0, 0.0);
            }
            """;
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource(source, "Evaluate", new(1, 1, 1),
            [(new uint[8], ResourceBindingType.BufferUav, 4)]);
        float[] values = result[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.All(values, value => Assert.True(float.IsFinite(value)));
        for (int index = 1; index < 5; index++)
            Assert.InRange(values[index], float.Epsilon, values[index - 1]);
        Assert.True(values[4] < values[3] * .001f);
        Assert.All(values.Skip(5), value => Assert.Equal(0, value));
    }
}
