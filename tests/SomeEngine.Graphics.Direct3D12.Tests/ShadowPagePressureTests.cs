using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowPagePressureTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void WorkPressureUsesEachViewsClusterContribution()
    {
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "vsm_work_throttle.slang"
            RWStructuredBuffer<float2> Bias;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadWorkBias()
            {
                float globalPressure = CalculateVirtualShadowWorkPressure(uint2(11000, 0), 0.0, 0.1, -1.0);
                uint2 maximum = uint2(10000, 0);
                float hot = CalculateVirtualShadowViewWorkPressure(uint2(10000, 0), maximum,
                    globalPressure, 0.0, 0.9);
                float cool = CalculateVirtualShadowViewWorkPressure(uint2(1000, 0), maximum,
                    globalPressure, 0.0, 0.9);
                Bias[0] = float2(hot, cool) * 2.0;
            }
            """, "ReadWorkBias", new(1, 1, 1), [(new uint[2], ResourceBindingType.BufferUav, 8)]);
        Assert.InRange(BitConverter.UInt32BitsToSingle(observed[0][0]), .0399999f, .0400001f);
        Assert.InRange(BitConverter.UInt32BitsToSingle(observed[0][1]), .0039999f, .0040001f);
    }

    [Fact]
    public void AvailablePageCapacityPreservesResolutionWhenRasterWorkGrows()
    {
        const uint capacity = 10;
        uint[] lists = new uint[4 * (capacity + 1)];
        // The AVAILABLE list holds the complete reusable page pool.
        lists[2 * capacity + 1] = capacity;
        uint[] settings = [capacity, 1, BitConverter.SingleToUInt32Bits(.85f),
            BitConverter.SingleToUInt32Bits(2), BitConverter.SingleToUInt32Bits(2), 1, 0, 0];
        uint[][] observed = new D3D12TestCompute(shaderCompiler).Execute(
            "vsm_dynamic_resolution.slang", "UpdateVirtualShadowDynamicResolution", new(1, 1, 1),
            [new BufferInput("DynamicResolution", settings, ResourceBindingType.ConstantBuffer, 0),
             new BufferInput("ShadowPageLists", lists, ResourceBindingType.BufferSrv, 4),
             new BufferInput("PolicyRasterWorkCount", [128], ResourceBindingType.BufferSrv, 4),
             new BufferInput("AllocationFailures", [0], ResourceBindingType.BufferSrv, 4),
             new BufferInput("DynamicResolutionBias", new uint[4], ResourceBindingType.BufferUav, 16)]);
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][1]));
    }
}
