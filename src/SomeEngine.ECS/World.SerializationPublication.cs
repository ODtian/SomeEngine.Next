namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Publishes a semantically identical successor after an admitted serializer has retained the
    /// source root. The serializer reads that root explicitly; ordinary World access always
    /// resolves the successor and therefore pays no serialization-context branch. This is a
    /// storage-ownership handoff, not a topology fact change, so both fact versions stay unchanged.
    /// </summary>
    internal void PublishSerializationSuccessor(
        WorldStructureRoot expectedSource,
        WorldStructureRoot successor)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(successor);
        if (ReferenceEquals(expectedSource, successor))
            throw new ArgumentException("Serialization successor must be a distinct root.", nameof(successor));

        if (FindStructuralCandidate(this, t_candidateContext) is not null)
        {
            throw new InvalidOperationException(
                "A serialization successor cannot publish inside a structural candidate.");
        }

        WorldStructurePublication current = Volatile.Read(ref _publishedStructure);
        if (!ReferenceEquals(current.Root, expectedSource))
        {
            throw new InvalidOperationException(
                "Serialization source root changed before successor publication.");
        }

        // Root identity and its unchanged fact epoch are released as one publication object.
        // `_lastStructuralCandidatePublicationEpoch` deliberately remains untouched: this is not
        // a deferred structural-candidate commit and must not make one appear published.
        Volatile.Write(
            ref _publishedStructure,
            new WorldStructurePublication(successor, current.Epoch));
    }
}
