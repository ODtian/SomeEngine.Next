using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Immutable shared-component indices for one chunk bucket.
/// </summary>
/// <remarks>
/// The backing is installed once when a shared chunk is created and is then shared by every
/// persistent chunk shell. Shared-component changes move an entity to a different chunk instead
/// of mutating this tuple in place, so chunk COW never has to copy it.
/// </remarks>
internal sealed class SharedComponentTuple : IEquatable<SharedComponentTuple>
{
    private readonly int[] _values;
    private readonly uint _hash;

    internal SharedComponentTuple(ReadOnlySpan<int> values)
    {
        _values = values.ToArray();
        _hash = StableHash.Compute(_values);
    }

    internal int Length => _values.Length;

    internal int this[int index] => _values[index];

    internal ReadOnlySpan<int> AsSpan() => _values;

    public bool Equals(SharedComponentTuple? other) =>
        ReferenceEquals(this, other) ||
        (other is not null &&
         _hash == other._hash &&
         _values.AsSpan().SequenceEqual(other._values));

    public override bool Equals(object? obj) =>
        obj is SharedComponentTuple other && Equals(other);

    public override int GetHashCode() => (int)_hash;
}

/// <summary>
/// Allocation index for every physical chunk which uses one canonical shared-value tuple.
/// </summary>
/// <remarks>
/// Only non-full chunks are retained in <see cref="OpenChunkSpan"/>. <see cref="ChunkCount"/> and
/// <see cref="LastCapacity"/> retain the physical-bucket lifetime and growth state when every
/// chunk is full; they do not participate in shared-value lifetime or index reclamation.
/// </remarks>
internal sealed class SharedChunkBucket
{
    private readonly List<Chunk> _openChunks;

    internal SharedChunkBucket(SharedComponentTuple values)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        _openChunks = new List<Chunk>(1);
    }

    private SharedChunkBucket(
        SharedComponentTuple values,
        int chunkCount,
        int lastCapacity,
        int openCapacity)
    {
        Values = values;
        ChunkCount = chunkCount;
        LastCapacity = lastCapacity;
        _openChunks = new List<Chunk>(openCapacity);
    }

    internal SharedComponentTuple Values { get; }

    internal ReadOnlySpan<Chunk> OpenChunkSpan => CollectionsMarshal.AsSpan(_openChunks);

    internal int OpenChunkCount => _openChunks.Count;

    internal Chunk OpenChunkAt(int index) => _openChunks[index];

    internal int ChunkCount { get; private set; }

    internal int LastCapacity { get; private set; }

    internal Chunk NextOpenChunk => _openChunks[^1];

    internal void Register(Chunk chunk)
    {
        RequireCanonical(chunk);

        ChunkCount++;
        LastCapacity = chunk.Capacity;
        if (!chunk.IsFull)
            _openChunks.Add(chunk);
    }

    internal void MarkFull(Chunk chunk)
    {
        RequireCanonical(chunk);
        if (!chunk.IsFull)
            throw new InvalidOperationException("A non-full shared chunk cannot leave the open index.");
        if (!_openChunks.Remove(chunk))
            throw new InvalidOperationException("A shared chunk can leave the open index only once.");
    }

    internal void MarkOpen(Chunk chunk)
    {
        RequireCanonical(chunk);
        if (chunk.IsFull)
            throw new InvalidOperationException("A full shared chunk cannot enter the open index.");
        if (!_openChunks.Contains(chunk))
            _openChunks.Add(chunk);
    }

    internal void Unregister(Chunk chunk)
    {
        RequireCanonical(chunk);
        if (ChunkCount <= 0)
            throw new InvalidOperationException("A shared chunk bucket cannot unregister past zero chunks.");

        _openChunks.Remove(chunk);
        ChunkCount--;
    }

    internal SharedChunkBucket CloneEmpty()
    {
        return new SharedChunkBucket(
            Values,
            ChunkCount,
            LastCapacity,
            _openChunks.Capacity);
    }

    internal void AddClonedOpenChunk(Chunk chunk)
    {
        RequireCanonical(chunk);
        if (chunk.IsFull)
            throw new InvalidOperationException("A full shared chunk cannot be cloned into the open index.");
        _openChunks.Add(chunk);
    }

    private void RequireCanonical(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!ReferenceEquals(chunk.SharedValues, Values))
        {
            throw new InvalidOperationException(
                "A shared chunk bucket and its chunks must use the same canonical tuple.");
        }
    }
}

/// <summary>
/// Supports allocation-free shared-bucket lookup from a transient value span while retaining the
/// canonical immutable tuple as the dictionary key.
/// </summary>
internal sealed class SharedComponentTupleComparer :
    IEqualityComparer<SharedComponentTuple>,
    IAlternateEqualityComparer<ReadOnlySpan<int>, SharedComponentTuple>
{
    internal static readonly SharedComponentTupleComparer Instance = new();

    private SharedComponentTupleComparer()
    {
    }

    public bool Equals(SharedComponentTuple? x, SharedComponentTuple? y) =>
        ReferenceEquals(x, y) || (x is not null && x.Equals(y));

    public int GetHashCode(SharedComponentTuple obj) => obj.GetHashCode();

    public SharedComponentTuple Create(ReadOnlySpan<int> alternate) => new(alternate);

    public bool Equals(ReadOnlySpan<int> alternate, SharedComponentTuple other) =>
        alternate.SequenceEqual(other.AsSpan());

    public int GetHashCode(ReadOnlySpan<int> alternate) =>
        (int)StableHash.Compute(alternate);
}
