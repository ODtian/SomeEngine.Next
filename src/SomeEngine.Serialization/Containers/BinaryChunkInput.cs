using System.IO.Compression;

namespace SomeEngine.Serialization.Containers;

/// <summary>One chunk's encoded bytes, independently retained after its source read completes.</summary>
public sealed class BinaryChunkInput
{
    private readonly byte[] _bytes;

    internal BinaryChunkInput(BinaryChunkEntry descriptor, byte[] bytes)
    {
        Descriptor = descriptor;
        _bytes = bytes;
    }

    public BinaryChunkEntry Descriptor { get; }
    public ReadOnlyMemory<byte> EncodedBytes => _bytes;

    /// <summary>Decodes and verifies this chunk directly into the caller's final storage without source I/O.</summary>
    public void DecodeInto(Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length != Descriptor.DecodedLength)
            throw new ArgumentException("The destination must match the chunk's decoded length.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        if (Descriptor.Compression == ChunkCompression.None)
            _bytes.AsSpan().CopyTo(destination);
        else
            DecodeBrotliInto(destination, cancellationToken);
        BinaryDocument.VerifyHash(destination, Descriptor.ContentHash, $"decoded chunk 0x{Descriptor.Key:X16}");
    }

    private void DecodeBrotliInto(Span<byte> destination, CancellationToken cancellationToken)
    {
        using var stored = new MemoryStream(_bytes, writable: false);
        using var decoder = new BrotliStream(stored, CompressionMode.Decompress);
        try
        {
            int written = 0;
            while (written < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = decoder.Read(destination[written..]);
                if (read == 0)
                    throw new InvalidDataException(
                        $"Chunk 0x{Descriptor.Key:X16} Brotli payload ended before its declared decoded length {Descriptor.DecodedLength}.");
                written = checked(written + read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (decoder.ReadByte() != -1)
                throw new InvalidDataException($"Chunk 0x{Descriptor.Key:X16} Brotli payload expands beyond its declared length.");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"Chunk 0x{Descriptor.Key:X16} contains invalid Brotli data.", exception);
        }
    }
}
