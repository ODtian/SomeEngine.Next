using System.IO.Compression;
using System.Security.Cryptography;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Containers;

public partial class BinaryDocument
{
    internal async ValueTask<ChunkLease> AcquirePagesAsync(BinaryChunkEntry descriptor, PageArena arena,
        ResidencyReservation reservation, CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        ValidateEntryBounds(descriptor);
        var pages = new PageBuffer(arena, descriptor.DecodedLength, arena.MaximumPageSize, reservation);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (descriptor.Compression == ChunkCompression.None)
            {
                await pages.FillAsync(async (offset, memory, token) =>
                {
                    await _source.ReadExactlyAsync(descriptor.Offset + offset, memory, token).ConfigureAwait(false);
                    hash.AppendData(memory.Span);
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var stored = new RangeSourceReadStream(_source, descriptor.Offset, descriptor.StoredLength);
                using var decoder = new BrotliStream(stored, CompressionMode.Decompress, leaveOpen: true);
                try
                {
                    await pages.FillAsync(async (_, memory, token) =>
                    {
                        await decoder.ReadExactlyAsync(memory, token).ConfigureAwait(false);
                        hash.AppendData(memory.Span);
                    }, cancellationToken).ConfigureAwait(false);
                    byte[] probe = new byte[1];
                    if (await decoder.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException($"Chunk 0x{descriptor.Key:X16} expands beyond its declared length.");
                }
                catch (EndOfStreamException exception)
                {
                    throw new InvalidDataException($"Chunk 0x{descriptor.Key:X16} ended before its declared decoded length.", exception);
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidDataException($"Chunk 0x{descriptor.Key:X16} contains invalid Brotli data.", exception);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceGeneration();
            if (!descriptor.ContentHash.FixedTimeEquals(Digest256.Finish(hash)))
                throw new InvalidDataException($"Decoded chunk 0x{descriptor.Key:X16} failed its content hash check.");
            return new ChunkLease(descriptor, pages);
        }
        catch { pages.Dispose(); throw; }
    }
}
