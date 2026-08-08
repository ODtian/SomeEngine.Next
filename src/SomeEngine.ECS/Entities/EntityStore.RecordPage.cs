namespace SomeEngine.ECS.Entities;

internal sealed partial class EntityStore
{
    private sealed class EntityRecordPage
    {
        internal EntityRecordPage(
            long identity,
            long ownerIdentity,
            long version,
            PersistentEntityRecord[] records)
        {
            Identity = identity;
            OwnerIdentity = ownerIdentity;
            Version = version;
            Records = records;
        }

        internal long Identity { get; }
        internal long OwnerIdentity { get; }
        internal long Version { get; }
        internal PersistentEntityRecord[] Records { get; }
    }
}
