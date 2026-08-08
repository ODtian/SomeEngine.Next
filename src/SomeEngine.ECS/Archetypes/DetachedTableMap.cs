using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Reference-identity mapping from one table image to its detached copy.
/// Shape equality is deliberately insufficient: records and cached transitions must be
/// remapped to the exact candidate object which cloned their source object.
/// </summary>
internal sealed class DetachedTableMap
{
    private readonly Dictionary<Archetype, Archetype> _archetypes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Chunk, Chunk> _chunks =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Archetype> _candidateArchetypes =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Chunk> _candidateChunks =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<long, Archetype>? _candidateArchetypesByIdentity = new();
    private Dictionary<long, Chunk>? _candidateChunksByIdentity = new();

    internal int ArchetypeCount => _archetypes.Count;

    internal int ChunkCount => _chunks.Count;

    internal int CandidateIdentityCount =>
        _candidateArchetypesByIdentity?.Count ??
        throw new InvalidOperationException(
            "Detached table identity resolvers have already transferred to EntityStore.");

    /// <summary>
    /// Transfers the root-local identity resolvers built during the table-clone traversal.
    /// DetachedTableMap keeps no alias to the mutable dictionaries after this ownership boundary.
    /// </summary>
    internal (
        Dictionary<long, Archetype> Archetypes,
        Dictionary<long, Chunk> Chunks) TakeCandidateIdentityResolvers()
    {
        Dictionary<long, Archetype> archetypes =
            _candidateArchetypesByIdentity ??
            throw new InvalidOperationException(
                "Detached table identity resolvers have already transferred to EntityStore.");
        Dictionary<long, Chunk> chunks =
            _candidateChunksByIdentity ??
            throw new InvalidOperationException(
                "Detached table identity resolvers have already transferred to EntityStore.");

        _candidateArchetypesByIdentity = null;
        _candidateChunksByIdentity = null;
        return (archetypes, chunks);
    }

    internal int ValidateTableRows(EntityStore store, Span<bool> visited)
    {
        ArgumentNullException.ThrowIfNull(store);

        int tableRowCount = 0;
        foreach ((Archetype source, Archetype candidate) in _archetypes)
        {
            tableRowCount +=
                store.ValidateMappedArchetypeRows(source, candidate, this, visited);
        }

        return tableRowCount;
    }

    internal void Add(Archetype source, Archetype candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);

        if (ReferenceEquals(source, candidate))
            throw new InvalidOperationException("A detached archetype must not alias its source.");
        if (!_candidateArchetypes.Add(candidate))
            throw new InvalidOperationException("A candidate archetype is already mapped from another source.");

        bool referenceAdded = false;
        bool identityAdded = false;
        try
        {
            _archetypes.Add(source, candidate);
            referenceAdded = true;
            (_candidateArchetypesByIdentity ??
                throw new InvalidOperationException(
                    "Cannot add mappings after identity resolver ownership transfer."))
                .Add(candidate.PersistentIdentity, candidate);
            identityAdded = true;
        }
        catch
        {
            if (referenceAdded)
                _archetypes.Remove(source);
            if (identityAdded)
                _candidateArchetypesByIdentity?.Remove(candidate.PersistentIdentity);
            _candidateArchetypes.Remove(candidate);
            throw;
        }
    }

    internal void Add(Chunk source, Chunk candidate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);

        if (ReferenceEquals(source, candidate))
            throw new InvalidOperationException("A detached chunk must not alias its source.");
        if (!_candidateChunks.Add(candidate))
            throw new InvalidOperationException("A candidate chunk is already mapped from another source.");

        bool referenceAdded = false;
        bool identityAdded = false;
        try
        {
            _chunks.Add(source, candidate);
            referenceAdded = true;
            (_candidateChunksByIdentity ??
                throw new InvalidOperationException(
                    "Cannot add mappings after identity resolver ownership transfer."))
                .Add(candidate.PersistentIdentity, candidate);
            identityAdded = true;
        }
        catch
        {
            if (referenceAdded)
                _chunks.Remove(source);
            if (identityAdded)
                _candidateChunksByIdentity?.Remove(candidate.PersistentIdentity);
            _candidateChunks.Remove(candidate);
            throw;
        }
    }

    internal Archetype Remap(Archetype source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _archetypes.TryGetValue(source, out var candidate)
            ? candidate
            : throw new InvalidOperationException(
                $"Archetype {source.ArchetypeId} does not belong to the detached table source image.");
    }

    internal Chunk Remap(Chunk source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _chunks.TryGetValue(source, out var candidate)
            ? candidate
            : throw new InvalidOperationException(
                "Chunk does not belong to the detached table source image.");
    }

    internal bool IsCandidate(Archetype archetype) =>
        _candidateArchetypes.Contains(archetype);

    internal bool IsCandidate(Chunk chunk) =>
        _candidateChunks.Contains(chunk);
}
