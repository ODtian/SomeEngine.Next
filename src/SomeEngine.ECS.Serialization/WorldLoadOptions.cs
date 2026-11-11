using System.Collections.Immutable;

namespace SomeEngine.ECS.Serialization;

public enum EntityIdentityMode : byte { Preserve, Remap }
public enum MissingReferenceMode : byte { KeepOriginal, Clear, Throw }

public readonly record struct WorldLoadOptions(
    EntityIdentityMode IdentityMode = EntityIdentityMode.Preserve,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw,
    SerializationReadLimits? ReadLimits = null,
    SavePurpose? RequiredPurpose = null,
    WorldExecution? Execution = null,
    bool RequireEndOfInput = false,
    ImmutableArray<ComponentCodec> Codecs = default);
