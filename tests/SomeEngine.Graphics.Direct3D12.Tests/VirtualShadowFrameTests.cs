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
        int[] lengths = [1, gridWords, 4, 80, 2, gridWords - 13];
        var inputs = lengths.Select(length => (
            Enumerable.Repeat(0xdeadbeefu, length).ToArray(), ResourceBindingType.BufferUav, 0u)).ToArray();
        uint[][] result = Execute("vsm_frame_reset.slang", "ResetVirtualShadowFrame",
            new DispatchArguments((uint)(gridWords + 255) / 256, 1, 1), inputs);
        foreach (uint[] buffer in result)
            Assert.All(buffer, word => Assert.Equal(0u, word));
    }
}
