using System.Collections.Immutable;

namespace SomeEngine.ECS.Serialization;

/// <summary>Controls the save format, write limits and caller-owned external key collection.</summary>
/// <param name="Purpose">The existing raw checkpoint or durable save format.</param>
/// <param name="MaximumSparseMemberships">Maximum sparse memberships; zero selects the existing unlimited path.</param>
/// <param name="MaximumTopologyRecords">Maximum hierarchy/relation rows, members and saved progress entries; zero selects the existing unlimited path.</param>
/// <param name="MaximumTopologyPayloadBytes">Maximum encoded topology bytes; zero selects the existing unlimited path.</param>
/// <param name="ExternalReferences">The caller's collection of nonempty keys, retained through background encoding completion.</param>
public readonly record struct SerializeOptions(
    SavePurpose Purpose = SavePurpose.RawCheckpoint,
    int MaximumSparseMemberships = 0,
    long MaximumTopologyRecords = 0,
    long MaximumTopologyPayloadBytes = 0,
    ISet<ExternalReferenceKey>? ExternalReferences = null,
    ImmutableArray<ComponentCodec> Codecs = default);
