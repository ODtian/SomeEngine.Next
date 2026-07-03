using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Systems;

public enum AccessConflictKind : byte
{
    ResourceWrite,
    ExclusiveStage,
}

public readonly record struct SystemAccessConflict(
    SystemAccessResource Resource,
    QueryAccess LeftAccess,
    QueryAccess RightAccess,
    AccessConflictKind Kind);

public static class AccessConflicts
{
    public static bool Conflicts(SystemAccessManifest left, SystemAccessManifest right) =>
        TryGetConflict(left, right, out _);

    public static bool TryGetConflict(
        SystemAccessManifest left,
        SystemAccessManifest right,
        out SystemAccessConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.RequiresExclusiveStage || right.RequiresExclusiveStage)
        {
            conflict = new SystemAccessConflict(
                FindExclusiveResource(left, right),
                QueryAccess.ReadWrite,
                QueryAccess.ReadWrite,
                AccessConflictKind.ExclusiveStage);
            return true;
        }

        var leftEntries = left.EntriesSpan;
        var rightEntries = right.EntriesSpan;
        for (int i = 0; i < leftEntries.Length; i++)
        {
            var leftEntry = leftEntries[i];
            if (leftEntry.Resource.Kind == AccessResourceKind.CommandBuffer)
                continue;

            for (int j = 0; j < rightEntries.Length; j++)
            {
                var rightEntry = rightEntries[j];
                if (rightEntry.Resource.Kind == AccessResourceKind.CommandBuffer)
                    continue;

                if (leftEntry.Resource == rightEntry.Resource &&
                    (CanWrite(leftEntry.Access) || CanWrite(rightEntry.Access)))
                {
                    conflict = new SystemAccessConflict(
                        leftEntry.Resource,
                        leftEntry.Access,
                        rightEntry.Access,
                        AccessConflictKind.ResourceWrite);
                    return true;
                }
            }
        }

        conflict = default;
        return false;
    }

    private static SystemAccessResource FindExclusiveResource(
        SystemAccessManifest left,
        SystemAccessManifest right)
    {
        if (TryStructuralResource(left, out var resource))
            return resource;

        if (TryStructuralResource(right, out resource))
            return resource;

        if (TryVisibleResource(left, out resource))
            return resource;

        if (TryVisibleResource(right, out resource))
            return resource;

        return SystemAccessResource.Structural;
    }

    private static bool TryStructuralResource(
        SystemAccessManifest manifest,
        out SystemAccessResource resource)
    {
        foreach (var entry in manifest.Entries)
        {
            if (entry.Resource.Kind == AccessResourceKind.Structural)
            {
                resource = entry.Resource;
                return true;
            }
        }

        resource = default;
        return false;
    }

    private static bool TryVisibleResource(
        SystemAccessManifest manifest,
        out SystemAccessResource resource)
    {
        foreach (var entry in manifest.Entries)
        {
            if (entry.Resource.Kind != AccessResourceKind.CommandBuffer)
            {
                resource = entry.Resource;
                return true;
            }
        }

        resource = default;
        return false;
    }

    private static bool CanWrite(QueryAccess access) =>
        access == QueryAccess.Write || access == QueryAccess.ReadWrite;
}

