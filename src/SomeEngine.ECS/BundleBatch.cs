using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public ref struct BundleBatch
{
    private Owners.Bundles _bundles;
    private BundleSpawnMap _plan;
    private BundleBatchChunk[]? _chunks;
    private int _chunkCount;
    private bool _completed;

    internal BundleBatch(Owners.Bundles bundles, BundleSpawnMap plan, BundleBatchChunk[]? chunks, int chunkCount, int count)
    {
        _bundles = bundles;
        _plan = plan;
        _chunks = chunks;
        _chunkCount = chunkCount;
        Count = count;
        _completed = false;
    }

    public int Count { get; }

    public ReadOnlySpan<BundleBatchChunk> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunks is null ? ReadOnlySpan<BundleBatchChunk>.Empty : _chunks.AsSpan(0, _chunkCount);
    }

    public void Complete()
    {
        if (_completed || _chunks is null)
            return;

        _bundles.CompleteBatch(_plan, _chunks.AsSpan(0, _chunkCount));
        _completed = true;
    }

    public void Dispose()
    {
        var chunks = _chunks;
        if (chunks is null)
            return;

        Complete();
        chunks.AsSpan().Clear();
        ArrayPool<BundleBatchChunk>.Shared.Return(chunks);
        _chunks = null;
        _chunkCount = 0;
    }
}

public readonly struct BundleBatchChunk
{
    private readonly BundleSpawnMap _plan;
    private readonly Chunk _chunk;
    private readonly int _startRow;

    internal BundleBatchChunk(BundleSpawnMap plan, Chunk chunk, int startRow, int count)
    {
        _plan = plan;
        _chunk = chunk;
        _startRow = startRow;
        Count = count;
    }

    public int Count { get; }

    internal int StartRow => _startRow;

    public ReadOnlySpan<Entity> Entities
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunk.Entities.AsSpan(_startRow, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Write<T>()
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        int columnIndex = _plan.Column(componentId);
        if (columnIndex < 0)
            ThrowMissing<T>();

        return Unsafe.As<T[]>(_chunk.Columns[columnIndex]).AsSpan(_startRow, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Array GetColumnStorage(int columnIndex)
    {
        return (Array)_chunk.Columns[columnIndex];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissing<T>()
    {
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} is not part of this bundle batch.");
    }
}

