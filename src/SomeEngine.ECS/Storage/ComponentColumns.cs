namespace SomeEngine.ECS.Storage;

internal sealed class ComponentColumns
{
    internal readonly ComponentType[] Components, Managed, Tracked, OwnedValues, Indexed, Shared, Groupable, Initializers, Computed, Cleanup;
    internal readonly PodColumn[] Pod;
    internal readonly bool RetainsOnDestroy, HasEntityReferences;
    internal readonly bool CanCopyPodAndBuffers;

    internal ComponentColumns(int[] types)
    {
        Components = types.Select(ComponentTypes.Get).ToArray();
        Pod = Components.Where(t => !t.IsTag && !t.IsShared && !t.ContainsReferences)
            .Select(t => new PodColumn(t.Id, t.Size)).ToArray();
        Managed = Components.Where(t => !t.IsTag && !t.IsShared && t.ContainsReferences).ToArray();
        Tracked = Components.Where(t => t.TracksWrites).ToArray();
        OwnedValues = Components.Where(t => t.HasValueOperations && !t.IsShared).ToArray();
        Indexed = Components.Where(t => t.IsIndexed).ToArray();
        Shared = Components.Where(t => t.IsShared).ToArray();
        Groupable = Components.Where(t => t.IsGrouped).ToArray();
        Initializers = Components.Where(t => t.InitializesEntity).ToArray();
        Computed = Components.Where(t => t.IsComputed).ToArray();
        Cleanup = Components.Where(t => t.IsCleanup).ToArray();
        RetainsOnDestroy = Components.Any(t => t.IsCleanup || t.KeepsRemoved);
        HasEntityReferences = Components.Any(t => t.HasEntityReferences);
        CanCopyPodAndBuffers = Computed.Length == 0 && Shared.Length == 0
            && OwnedValues.All(t => t.HasColumnRelease) && Managed.All(t => t.HasColumnRelease);
    }
}
