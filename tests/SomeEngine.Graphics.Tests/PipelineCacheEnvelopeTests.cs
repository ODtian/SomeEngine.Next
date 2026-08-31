namespace SomeEngine.Graphics.Tests;

using Xunit;

public sealed class PipelineCacheEnvelopeTests
{
    private static readonly PipelineCacheLimits Unlimited = new(
        PipelineCacheEnvelope.HardEntryCountLimit,
        int.MaxValue,
        int.MaxValue);

    [Fact]
    public void Schema_three_round_trip_is_canonical_and_preserves_backend_entries()
    {
        PipelineCacheEntry vulkan = Entry(2, 1, 0x22, 0x32, [4, 5, 6]);
        PipelineCacheEntry direct3D = Entry(1, 2, 0x11, 0x31, [1, 2, 3]);

        byte[] first = PipelineCacheEnvelope.Serialize(
            [vulkan, direct3D],
            Unlimited,
            CancellationToken.None);
        byte[] second = PipelineCacheEnvelope.Serialize(
            [direct3D, vulkan],
            Unlimited,
            CancellationToken.None);
        ParsedPipelineCache parsed = PipelineCacheEnvelope.Parse(
            first,
            Unlimited,
            CancellationToken.None);

        Assert.Equal(second, first);
        Assert.Equal("SERHIC01"u8.ToArray(), first[..8]);
        Assert.Equal(2, parsed.Entries.Length);
        Assert.Equal(1UL, parsed.Entries[0].Backend);
        Assert.Equal(2UL, parsed.Entries[1].Backend);
        Assert.True(parsed.TryGetCompatibleEntry(
            vulkan.Backend,
            vulkan.Family,
            vulkan.Key,
            vulkan.Compatibility,
            out PipelineCacheEntry restored));
        Assert.Equal(vulkan.Payload, restored.Payload);
    }

    [Fact]
    public void Parse_rejects_corruption_and_all_three_policy_limits()
    {
        byte[] envelope = PipelineCacheEnvelope.Serialize(
            [Entry(1, 1, 0x10, 0x20, [1, 2, 3, 4])],
            Unlimited,
            CancellationToken.None);
        byte[] corrupt = (byte[])envelope.Clone();
        corrupt[^1] ^= 0x80;

        GraphicsException checksum = Assert.Throws<GraphicsException>(() =>
            PipelineCacheEnvelope.Parse(corrupt, Unlimited, CancellationToken.None));
        Assert.Equal(GraphicsError.NativeFailure, checksum.Error);
        Assert.Throws<ArgumentException>(() => PipelineCacheEnvelope.Parse(
            envelope,
            Unlimited with { MaximumEntryCount = 0 },
            CancellationToken.None));
        Assert.Throws<ArgumentException>(() => PipelineCacheEnvelope.Parse(
            envelope,
            Unlimited with { MaximumByteCount = envelope.Length - 1 },
            CancellationToken.None));
        Assert.Throws<ArgumentException>(() => PipelineCacheEnvelope.Parse(
            envelope,
            Unlimited with { MaximumDecodedByteCount = 3 },
            CancellationToken.None));
    }

    [Fact]
    public void Merge_is_order_independent_and_uses_canonical_payload_tie_breaking()
    {
        PipelineCacheEntry larger = Entry(7, 1, 0x40, 0x50, [2]);
        PipelineCacheEntry smaller = Entry(7, 1, 0x40, 0x50, [1]);
        PipelineCacheEntry extra = Entry(8, 1, 0x41, 0x51, [9]);

        PipelineCacheEntry[] left = PipelineCacheEnvelope.Merge(
            [larger],
            [extra, smaller],
            Unlimited,
            CancellationToken.None);
        PipelineCacheEntry[] right = PipelineCacheEnvelope.Merge(
            [smaller, extra],
            [larger],
            Unlimited,
            CancellationToken.None);

        Assert.Equal(
            PipelineCacheEnvelope.Serialize(left, Unlimited, CancellationToken.None),
            PipelineCacheEnvelope.Serialize(right, Unlimited, CancellationToken.None));
        PipelineCacheEntry selected = Assert.Single(left, entry => entry.Backend == 7);
        Assert.Equal([1], selected.Payload);
    }

    private static PipelineCacheEntry Entry(
        ulong backend,
        byte family,
        byte key,
        byte compatibility,
        byte[] payload) =>
        new(
            backend,
            family,
            Enumerable.Repeat(key, PipelineCacheEnvelope.HashByteCount).ToArray(),
            Enumerable.Repeat(compatibility, PipelineCacheEnvelope.HashByteCount).ToArray(),
            payload);
}
