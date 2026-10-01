using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowFrameTests
{
    [Theory]
    [InlineData(257)]
    [InlineData(1025)]
    public void FrameResetClearsGridAndCountersWithDifferentLengths(int gridWords)
    {
        BufferInput[] inputs =
        {
            new("ActivePageCount", Enumerable.Repeat(0xdeadbeefu, 1).ToArray(), ResourceBindingType.BufferUav, 0),
            new("UncachedPageRectBounds", Enumerable.Repeat(0xdeadbeefu, 12).ToArray(), ResourceBindingType.BufferUav, 16),
            new("AllocatedPageRectBounds", Enumerable.Repeat(0xdeadbeefu, 20).ToArray(), ResourceBindingType.BufferUav, 16),
            new("RasterPageMasks", Enumerable.Repeat(0xdeadbeefu, gridWords).ToArray(), ResourceBindingType.BufferUav, 0),
            new("AllocatedPageHierarchy", Enumerable.Repeat(0xdeadbeefu, 4).ToArray(), ResourceBindingType.BufferUav, 0),
            new("ClusterDispatchArguments", Enumerable.Repeat(0xdeadbeefu, 80).ToArray(), ResourceBindingType.BufferUav, 0),
            new("PageDispatchArguments", Enumerable.Repeat(0xdeadbeefu, 2).ToArray(), ResourceBindingType.BufferUav, 0),
            new("PageRequestDispatchArguments", Enumerable.Repeat(0xdeadbeefu, gridWords - 13).ToArray(), ResourceBindingType.BufferUav, 0),
            new("AllocationFailures", Enumerable.Repeat(0xdeadbeefu, 7).ToArray(), ResourceBindingType.BufferUav, 0),
            new("PolicyRasterWorkCount", Enumerable.Repeat(0xdeadbeefu, 33).ToArray(), ResourceBindingType.BufferUav, 0),
        };
        uint[][] result = Execute("vsm_frame_reset.slang", "ResetVirtualShadowFrame",
            new DispatchArguments((uint)(gridWords + 255) / 256, 1, 1), inputs);
        foreach (int index in new[] { 0, 3, 4, 5, 6, 7, 8, 9 })
            Assert.All(result[index], word => Assert.Equal(0u, word));
    }
}
