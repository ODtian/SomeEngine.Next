using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class MappedBufferReadTests
{
    [Fact]
    public void ReadPreservesBytesAcrossOffsetsLengthsAndDestinationAlignment()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(device,
            new BufferDesc(8192, BufferUsages.CopySource), MemoryType.Upload);
        byte[] expected = Enumerable.Range(0, 8192).Select(index => unchecked((byte)(index * 31 + 7))).ToArray();
        using (MappedBuffer write = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            expected.CopyTo(write.Bytes);
            write.Flush(write.Range);
        }
        using MappedBuffer mapping = backend.Map(upload, MapType.Read, new BufferRange(7, 8000));
        int[] lengths = [0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 4097];
        byte[] destination = new byte[4200];
        for (int offset = 0; offset < 32; offset++)
            foreach (int length in lengths)
                for (int alignment = 1; alignment <= 16; alignment++)
                {
                    Array.Fill(destination, (byte)0xcd);
                    mapping.Read(destination.AsSpan(alignment, length), offset);
                    Assert.True(expected.AsSpan(7 + offset, length).SequenceEqual(destination.AsSpan(alignment, length)));
                    Assert.Equal(0xcd, destination[alignment - 1]);
                    Assert.Equal(0xcd, destination[alignment + length]);
                }
        mapping.Read(destination.AsSpan(0, 17), 7983);
        Assert.True(expected.AsSpan(7990, 17).SequenceEqual(destination.AsSpan(0, 17)));
        mapping.Read(Span<byte>.Empty, 8000);
    }

    [Fact]
    public void ReadWithOverlapKeepsMoveSemanticsInBothDirections()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(device,
            new BufferDesc(256, BufferUsages.CopySource), MemoryType.Upload);
        using MappedBuffer mapping = backend.Map(upload, MapType.ReadWrite, BufferRange.Whole);
        byte[] values = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        byte[] expected = (byte[])values.Clone();
        values.CopyTo(mapping.Bytes);
        expected.AsSpan(0, 180).CopyTo(expected.AsSpan(7));
        mapping.Read(mapping.Bytes.Slice(7, 180));
        Assert.True(expected.AsSpan().SequenceEqual(mapping.Bytes));
        values.CopyTo(mapping.Bytes);
        values.CopyTo(expected, 0);
        expected.AsSpan(7, 180).CopyTo(expected);
        mapping.Read(mapping.Bytes[..180], 7);
        Assert.True(expected.AsSpan().SequenceEqual(mapping.Bytes));
    }

    [Fact]
    public void ReadRejectsInvalidRangesAndReleasedMappings()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        MappedBuffer mapping = backend.Map(upload, MapType.Read, BufferRange.Whole);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(new byte[1], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(new byte[17], 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Read(Span<byte>.Empty, 65));
        mapping.Dispose();
        Assert.Throws<InvalidOperationException>(() => mapping.Read(new byte[1]));
        Assert.Throws<InvalidOperationException>(() => mapping.Read(Span<byte>.Empty));
        Assert.Throws<InvalidOperationException>(() => default(MappedBuffer).Read(new byte[1]));
    }
}
