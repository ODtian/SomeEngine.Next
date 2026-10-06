using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowPagePressureTests(D3D12TestCompiler shaderCompiler)
{
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
