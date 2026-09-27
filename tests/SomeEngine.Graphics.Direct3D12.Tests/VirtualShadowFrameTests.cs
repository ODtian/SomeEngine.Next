using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowFrameTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    public void ReceiverMaskResetChangesOnlyRequestedPages(int requestCount)
    {
        const int pageCount = 137;
        uint[] indices = Enumerable.Range(0, 65).Select(index => (uint)((index * 17) % pageCount)).ToArray();
        uint[] masks = Enumerable.Range(0, pageCount * 2).Select(index => (uint)(index + 1)).ToArray();
        uint[][] result = Execute("vsm_receiver_mask_reset.slang", "ResetVirtualShadowReceiverMasks",
            new DispatchArguments((uint)Math.Max(1, (requestCount + 63) / 64), 1, 1),
            [([0, 1, 1, (uint)requestCount], ResourceBindingType.BufferSrv, 0),
             (indices, ResourceBindingType.BufferSrv, 4), (masks, ResourceBindingType.BufferUav, 8)]);
        HashSet<uint> changed = indices.Take(requestCount).ToHashSet();
        for (int word = 0; word < masks.Length; word++)
            Assert.Equal(changed.Contains((uint)(word / 2)) ? 0u : masks[word], result[0][word]);
    }

    [Theory]
    [InlineData(257)]
    [InlineData(1025)]
    public void FrameResetClearsGridAndCountersWithDifferentLengths(int gridWords)
    {
        int[] lengths = [1, 80, 80, gridWords, gridWords - 13, 3, 3, 3, 2, 1];
        var inputs = lengths.Select((length, index) => (
            Enumerable.Repeat(0xdeadbeefu, length).ToArray(), ResourceBindingType.BufferUav, index is 1 or 2 ? 16u : 0u)).ToArray();
        uint[][] result = Execute("vsm_frame_reset.slang", "ResetVirtualShadowFrame",
            new DispatchArguments((uint)(gridWords + 255) / 256, 1, 1), inputs);
        for (int index = 0; index < result.Length; index++)
            for (int word = 0; word < result[index].Length; word++)
                Assert.Equal(index is 1 or 2 && word % 4 < 2 ? uint.MaxValue : 0u, result[index][word]);
    }
}
