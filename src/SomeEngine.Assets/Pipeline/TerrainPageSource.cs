using SomeEngine.Serialization;

namespace SomeEngine.Assets.Pipeline;

/// <summary>Independent field-page file access; returned CPU samples have ordinary managed lifetimes.</summary>
public sealed class TerrainPageSource : IDisposable, ITerrainFileSource
{
    private readonly AssetBlockSource? _source;
    private readonly IReadOnlyDictionary<ulong, ReadOnlyMemory<byte>> _chunks;
    private bool _disposed;

    internal TerrainPageSource(AssetBlock block)
    {
        _source = block.RetainSource();
        _chunks = block.Chunks.ToDictionary();
    }

    private TerrainPageSource(TerrainPageSource source)
    {
        lock (source)
        {
            ObjectDisposedException.ThrowIf(source._disposed, source);
            _source = source._source?.Retain();
            _chunks = source._chunks;
        }
    }

    public TerrainPageSource Retain() => new(this);
    ITerrainFileSource ITerrainFileSource.Retain() => Retain();

    async ValueTask ITerrainFileSource.ReadPageIntoAsync(ulong key, Memory<float> samples)
    {
        ReadOnlyMemory<byte> bytes = _chunks.TryGetValue(key, out ReadOnlyMemory<byte> local)
            ? local : await _source!.ReadBytesAsync(key).ConfigureAwait(false);
        var reader = new BinaryDataReader(bytes.Span);
        int count = reader.ReadCollectionCount("terrain samples", sizeof(float), 24);
        if (count != samples.Length) throw new InvalidDataException("Terrain page sample count differs from its field grid.");
        for (int index = 0; index < count; index++) samples.Span[index] = reader.ReadSingle();
        reader.EnsureFullyConsumed("terrain samples");
    }

    public async ValueTask<ReadOnlyMemory<float>> ReadPageAsync(ulong chunkKey, CancellationToken cancellationToken = default)
    {
        using TerrainPageSource read = Retain();
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> bytes = read._chunks.TryGetValue(chunkKey, out ReadOnlyMemory<byte> local)
            ? local : await read._source!.ReadBytesAsync(chunkKey, cancellationToken).ConfigureAwait(false);
        var reader = new BinaryDataReader(bytes.Span);
        int count = reader.ReadCollectionCount("terrain samples", sizeof(float), 24);
        var samples = new float[count];
        for (int index = 0; index < samples.Length; index++) samples[index] = reader.ReadSingle();
        reader.EnsureFullyConsumed("terrain samples");
        return samples;
    }

    internal static ReadOnlyMemory<byte> Encode(ReadOnlyMemory<float> samples)
    {
        var bytes = new byte[sizeof(int) + samples.Length * sizeof(float)];
        var writer = new BinaryDataWriter(bytes);
        writer.WriteInt32(samples.Length);
        foreach (float sample in samples.Span) writer.WriteSingle(sample);
        return bytes;
    }

    public void Dispose()
    {
        lock (this)
        {
            if (_disposed) return;
            _disposed = true;
            _source?.Dispose();
        }
    }
}
