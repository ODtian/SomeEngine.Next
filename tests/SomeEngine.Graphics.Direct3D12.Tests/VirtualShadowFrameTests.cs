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
        int rectangleWords = ((gridWords + 3) / 4) * 4;
        int[] lengths = [1, rectangleWords, rectangleWords, gridWords, gridWords - 13, 4, 80, 4, 2, 1];
        var inputs = lengths.Select((length, index) => (
            Enumerable.Repeat(0xdeadbeefu, length).ToArray(), ResourceBindingType.BufferUav,
            index is 1 or 2 ? 16u : 0u)).ToArray();
        uint[][] result = Execute("vsm_frame_reset.slang", "ResetVirtualShadowFrame",
            new DispatchArguments((uint)(gridWords + 255) / 256, 1, 1), inputs);
        for (int buffer = 0; buffer < result.Length; buffer++)
            for (int word = 0; word < result[buffer].Length; word++)
                Assert.Equal(buffer is 1 or 2 && word % 4 < 2 ? uint.MaxValue : 0u, result[buffer][word]);
    }
}
