using SomeEngine.Job;

namespace SomeEngine.ECS;

public sealed partial class World
{
    internal void CollectEventAccesses(ref AccessBuilder<JobResourceAccess> accesses, bool write)
    {
        if (_destroyedLog is { } destroyed) accesses.Add(write ? destroyed.Write : destroyed.Read);
        foreach (QueryData? query in _queryBindings)
            if (query?.MembershipLog is { } members) accesses.Add(write ? members.Write : members.Read);
    }
}
