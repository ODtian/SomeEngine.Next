namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const string DefaultScopeResourceName = "scope resource";
    private const string DefaultScopeTokenName = "scope token";
    internal JobResource CreateScopeResource(string? name)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        ScopeToken scope = s_currentScope;
        if (scope.Index == 0)
        {
            throw CreateCurrentScopeRequiredException(ResourceKind.Resource, name);
        }

        JobResource resource = _resources.CreateResource(name);
        AddScopeOwnedResource(
            scope.ToHandle(),
            new ScopeOwnedResource(resource.Id, resource.Version, resource.Generation, ResourceKind.Resource));
        return resource;
    }

    internal JobResourceToken CreateScopeResourceToken(string? name)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        ScopeToken scope = s_currentScope;
        if (scope.Index == 0)
        {
            throw CreateCurrentScopeRequiredException(ResourceKind.Token, name);
        }

        JobResourceToken token = _resources.CreateToken(name);
        AddScopeOwnedResource(
            scope.ToHandle(),
            new ScopeOwnedResource(token.Id, token.Version, token.Generation, ResourceKind.Token));
        return token;
    }

    private void AddScopeOwnedResource(JobHandle owner, ScopeOwnedResource resource)
    {
        if (!_scopes.AddResource(owner, resource))
        {
            ReleaseSingleScopeOwnedResource(resource);
        }
    }

    private void ReleaseSingleScopeOwnedResource(ScopeOwnedResource resource)
    {
        ReadOnlySpan<ScopeOwnedResource> resources = stackalloc ScopeOwnedResource[] { resource };
        _resources.ReleaseScopeOwned(resources);
    }

    private JobResourceSafetyException CreateCurrentScopeRequiredException(ResourceKind kind, string? name)
    {
        string resourceName = name ?? (kind == ResourceKind.Resource ? DefaultScopeResourceName : DefaultScopeTokenName);
        return new JobResourceSafetyException(
            $"Cannot create scope-owned {kind.ToString().ToLowerInvariant()} '{resourceName}' outside a running job scope.",
            _resources.SafetyMode,
            jobTypeName: null,
            resourceName,
            resourceId: 0,
            kind.ToString());
    }

    private void AttachToCurrentScope(JobHandle child)
    {
        ScopeToken parent = s_currentScope;
        if (parent.Index == 0)
        {
            return;
        }

        JobHandle handleToComplete = _scopes.AttachChild(parent, child);
        if (handleToComplete.Index != 0)
        {
            TryCompleteState(handleToComplete);
        }
    }

    private readonly struct ScopeToken
    {
        internal readonly int Index;
        internal readonly int Version;
        internal readonly long Generation;

        private ScopeToken(int index, int version, long generation)
        {
            Index = index;
            Version = version;
            Generation = generation;
        }

        internal static ScopeToken FromHandle(JobHandle handle)
        {
            return new ScopeToken(handle.Index, handle.Version, handle.Generation);
        }

        internal JobHandle ToHandle()
        {
            return new JobHandle(Index, Version, Generation);
        }
    }
}



