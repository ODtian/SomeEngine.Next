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
        int[] lengths = [1, 80, gridWords, gridWords - 13, 3, 3, 3, 2, 1];
        var inputs = lengths.Select((length, index) => (
            Enumerable.Repeat(0xdeadbeefu, length).ToArray(), ResourceBindingType.BufferUav, index == 1 ? 16u : 0u)).ToArray();
        uint[][] result = Execute("vsm_frame_reset.slang", "ResetVirtualShadowFrame",
            new DispatchArguments((uint)(gridWords + 255) / 256, 1, 1), inputs);
        for (int index = 0; index < result.Length; index++)
            for (int word = 0; word < result[index].Length; word++)
                Assert.Equal(index == 1 && word % 4 < 2 ? uint.MaxValue : 0u, result[index][word]);
    }
}
