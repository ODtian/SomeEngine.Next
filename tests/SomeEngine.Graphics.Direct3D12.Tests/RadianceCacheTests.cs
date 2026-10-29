using System;
using System.Runtime.InteropServices;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class RadianceCacheTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void NeuralCacheLearnsReflectedRadianceAcrossGpuUpdates()
    {
        const string source = """
            #include "gi_radiance_cache.slang"
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(8, 1, 1)]
            void SupplyTraining(uint index : SV_GroupIndex)
            {
                GiRadianceSample sample = (GiRadianceSample)0;
                sample.Position = float3(4, 2, 3);
                sample.Normal = float3(0, 1, 0);
                sample.Radiance = float3(1.4, 0.8, 0.25);
                sample.Valid = 1u;
                sample.Weight = 1.0;
                GiRadianceTraining[index] = sample;
            }
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadBefore()
            {
                Results[0] = float4(QueryGiNeuralRadiance(float3(4, 2, 3), float3(0, 1, 0)), 1.0);
            }
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadAfter()
            {
                Results[1] = QueryGiRadianceValue(float3(4, 2, 3), float3(0, 1, 0), 0.25);
            }
            """;
        var steps = new List<(string Entry, DispatchArguments Groups)>
        {
            ("InitializeGiRadianceCache", new(15, 1, 1)),
            ("SupplyTraining", new(1, 1, 1)),
            ("ReadBefore", new(1, 1, 1)),
        };
        for (int update = 0; update < 128; update++)
        {
            steps.Add(("EvaluateGiNeuralTraining", new(1, 1, 1)));
            steps.Add(("DifferentiateGiNeuralTraining", new(113, 1, 1)));
            steps.Add(("BeginGiNeuralUpdate", new(1, 1, 1)));
            steps.Add(("UpdateGiNeuralRadiance", new(15, 1, 1)));
        }
        steps.Add(("ReadAfter", new(1, 1, 1)));
        uint[] settings = [0, 8, 64, 2, BitConverter.SingleToUInt32Bits(.25f), BitConverter.SingleToUInt32Bits(.01f), 1, 0];
        uint[][] output = new D3D12TestCompute(shaderCompiler).ExecuteSource(source, steps.ToArray(),
            [(settings, ResourceBindingType.ConstantBuffer, 0),
             (new uint[64], ResourceBindingType.BufferUav, 4),
             (new uint[64 * 4], ResourceBindingType.BufferUav, 16),
             (new uint[64 * 4], ResourceBindingType.BufferUav, 16),
             (new uint[8 * 12], ResourceBindingType.BufferUav, 48),
             (new uint[899], ResourceBindingType.BufferUav, 4),
             (new uint[899 * 2], ResourceBindingType.BufferUav, 8),
             (new uint[8 * 96], ResourceBindingType.BufferUav, 4),
             (new uint[899 * 8], ResourceBindingType.BufferUav, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4),
             (new uint[8], ResourceBindingType.BufferUav, 16)]);
        ReadOnlySpan<float> values = MemoryMarshal.Cast<uint, float>(output[^1]);
        float[] expected = [1.4f, .8f, .25f];
        float before = 0, after = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            before += MathF.Abs(values[channel] - expected[channel]);
            after += MathF.Abs(values[4 + channel] - expected[channel]);
            Assert.InRange(values[4 + channel], expected[channel] - .08f, expected[channel] + .08f);
        }
        Assert.True(after < before * .2f);
        Assert.True(values[7] > 0.0f);
    }

    [Fact]
    public void CacheQueryReturnsTheMeanOfValidReflectedRadianceSamples()
    {
        const string source = """
            #include "gi_radiance_cache.slang"
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SupplySamples()
            {
                GiRadianceSample sample = (GiRadianceSample)0;
                sample.Position = float3(0.1, 0.2, 0.3);
                sample.Normal = float3(0, 1, 0);
                sample.Valid = 1u;
                sample.Weight = 1.0;
                sample.Radiance = float3(1, 2, 3);
                GiRadianceTraining[0] = sample;
                sample.Radiance = float3(3, 6, 5);
                GiRadianceTraining[1] = sample;
                sample.Valid = 0u;
                sample.Radiance = 1000.0;
                GiRadianceTraining[2] = sample;
            }
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadRadiance()
            {
                Results[0] = float4(QueryGiReflectedRadiance(float3(0.1, 0.2, 0.3), float3(0, 1, 0), 0.25), 1.0);
            }
            """;
        uint[] settings = [0, 3, 256, 3, BitConverter.SingleToUInt32Bits(.25f), BitConverter.SingleToUInt32Bits(.01f), 0, 0];
        uint[][] output = new D3D12TestCompute(shaderCompiler).ExecuteSource(source,
            [("SupplySamples", new(1, 1, 1)), ("UpdateGiHashedRadiance", new(1, 1, 1)),
             ("ResolveGiHashedRadiance", new(4, 1, 1)), ("ReadRadiance", new(1, 1, 1))],
            [(settings, ResourceBindingType.ConstantBuffer, 0),
             (new uint[256], ResourceBindingType.BufferUav, 4),
             (new uint[256 * 4], ResourceBindingType.BufferUav, 16),
             (new uint[256 * 4], ResourceBindingType.BufferUav, 16),
             (new uint[3 * 12], ResourceBindingType.BufferUav, 48),
             (new uint[899], ResourceBindingType.BufferUav, 4),
             (new uint[899 * 2], ResourceBindingType.BufferUav, 8),
             (new uint[3 * 96], ResourceBindingType.BufferUav, 4),
             (new uint[899 * 8], ResourceBindingType.BufferUav, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4),
             (new uint[4], ResourceBindingType.BufferUav, 16)]);
        ReadOnlySpan<float> value = MemoryMarshal.Cast<uint, float>(output[^1]);
        Assert.InRange(value[0], 1.999f, 2.001f);
        Assert.InRange(value[1], 3.999f, 4.001f);
        Assert.InRange(value[2], 3.999f, 4.001f);
    }
}
