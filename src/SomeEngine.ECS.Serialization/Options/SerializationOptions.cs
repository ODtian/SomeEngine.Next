namespace SomeEngine.ECS.Serialization;

public enum SnapshotPayloadKind : byte
{
    Component,
    Entity,
    EntitySet,
    QueryResult,
    World,
}

public enum EntityIdentityMode : byte
{
    Preserve,
    Remap,
}

public enum MissingReferenceMode : byte
{
    KeepOriginal,
    Clear,
    Throw,
}

/// <summary>
/// Defines the persistence contract carried by a serialized payload.
/// </summary>
public enum SerializationContract : byte
{
    /// <summary>
    /// A fast, ABI-bound checkpoint. It is accepted only by the exact build/runtime identity
    /// that produced it and may use native in-memory component layouts.
    /// </summary>
    RawCheckpoint,

    /// <summary>
    /// A durable, canonical payload. Every value must use a generated or explicitly registered
    /// canonical/custom codec; implicit native-layout codecs are rejected.
    /// </summary>
    DurableSave,
}

/// <summary>Controls serialization identity, persistence contract, and bounded write-side indexing.</summary>
/// <remarks>
/// Whole-World v4 item and topology payloads are encoded exactly once directly to the destination,
/// followed by their measured byte-count footer. Non-seekable output does not retain an encoded
/// item or topology payload backing.
/// </remarks>
/// <param name="Contract">The ABI-bound or durable wire contract.</param>
/// <param name="MaximumSparseMemberships">
/// Maximum total sparse component memberships that whole-World v4 serialization may index for
/// its canonical per-entity merge. Zero (the default) imposes no explicit limit;
/// memory-sensitive callers should set an application-appropriate positive bound.
/// </param>
/// <param name="MaximumTopologyRecords">
/// Maximum total hierarchy/relation records retained across topology capture. Zero (the default)
/// imposes no explicit limit.
/// </param>
/// <param name="MaximumTopologyPayloadBytes">
/// Maximum total topology bytes admitted while encoding caller-owned output. Zero (the default)
/// imposes no explicit limit.
/// </param>
public readonly record struct SerializeOptions(
    SerializationContract Contract = SerializationContract.RawCheckpoint,
    int MaximumSparseMemberships = 0,
    long MaximumTopologyRecords = 0,
    long MaximumTopologyPayloadBytes = 0);

public readonly record struct WorldLoadOptions(
    EntityIdentityMode IdentityMode = EntityIdentityMode.Preserve,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw,
    SerializationReadLimits? ReadLimits = null,
    SerializationContract? RequiredContract = null);

public readonly record struct SerializationReadOptions(
    SerializationReadLimits? ReadLimits = null,
    SerializationContract? RequiredContract = null);

