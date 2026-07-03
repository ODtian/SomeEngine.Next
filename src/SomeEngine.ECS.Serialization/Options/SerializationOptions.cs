namespace SomeEngine.ECS.Serialization;

public enum SnapshotPayloadKind : byte
{
    Component,
    Entity,
    EntitySet,
    QueryResult,
    World,
    Delta,
}

public enum SerializationPurpose : byte
{
    Snapshot,
    Patch,
    Scene,
    Prefab,
    Delta,
}

public enum EntityIdentityMode : byte
{
    Preserve,
    Remap,
    Omit,
}

public enum MissingReferenceMode : byte
{
    KeepOriginal,
    Clear,
    Throw,
}

public enum EntityApplyMode : byte
{
    MergeIncluded,
    ReplaceIncluded,
    ReplaceEntity,
}

public enum UnknownTypeMode : byte
{
    Throw,
    Skip,
}

public enum MissingComponentMode : byte
{
    Throw,
    Skip,
}

public enum SchemaMismatchMode : byte
{
    Throw,
    UseRegisteredMigration,
    BestEffortAdditive,
}

public enum DeltaEventKind : byte
{
    EntityCreated,
    EntityDestroyed,
    ComponentAdded,
    ComponentRemoved,
    ComponentChanged,
    TagAdded,
    TagRemoved,
    EnabledChanged,
    SharedChanged,
    BufferChanged,
    SparseAdded,
    SparseRemoved,
    SparseChanged,
    RelationAdded,
    RelationRemoved,
    RelationChanged,
    BufferAdded,
    BufferRemoved,
    SharedAdded,
    SharedRemoved,
}

public readonly record struct SerializeOptions(
    SerializationPurpose Purpose = SerializationPurpose.Snapshot,
    EntityIdentityMode IdentityMode = EntityIdentityMode.Preserve);

public readonly record struct EntityApplyOptions(
    EntityApplyMode ApplyMode = EntityApplyMode.MergeIncluded,
    EntityIdentityMode IdentityMode = EntityIdentityMode.Preserve,
    UnknownTypeMode UnknownTypeMode = UnknownTypeMode.Throw,
    SchemaMismatchMode SchemaMismatchMode = SchemaMismatchMode.Throw,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw);

public readonly record struct EntityCreateOptions(
    EntityIdentityMode IdentityMode = EntityIdentityMode.Omit,
    UnknownTypeMode UnknownTypeMode = UnknownTypeMode.Throw,
    SchemaMismatchMode SchemaMismatchMode = SchemaMismatchMode.Throw,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw);

public readonly record struct WorldLoadOptions(
    EntityIdentityMode IdentityMode = EntityIdentityMode.Preserve,
    UnknownTypeMode UnknownTypeMode = UnknownTypeMode.Throw,
    SchemaMismatchMode SchemaMismatchMode = SchemaMismatchMode.Throw,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw);

public readonly record struct DeltaSerializeOptions(
    bool ClearJournal = false);

public readonly record struct DeltaEvent(
    DeltaEventKind Kind,
    global::SomeEngine.ECS.Entities.Entity Entity,
    int ComponentId,
    global::SomeEngine.ECS.Entities.Entity Target,
    uint Version);

