using System.Runtime.CompilerServices;
using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS;

internal sealed class ComponentWriteVersions
{
    private readonly Lock _gate = new();
    private ComponentVersion?[] _versions;

    internal ComponentWriteVersions() : this(new ComponentVersion?[8])
    {
    }

    private ComponentWriteVersions(ComponentVersion?[] versions) => _versions = versions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Read(int componentId)
    {
        ComponentVersion?[] versions = Volatile.Read(ref _versions);
        return componentId < versions.Length && Volatile.Read(ref versions[componentId]) is { } version
            ? unchecked((uint)Volatile.Read(ref version.Value))
            : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Advance(int componentId)
    {
        ComponentVersion?[] versions = Volatile.Read(ref _versions);
        ComponentVersion version = componentId < versions.Length &&
            Volatile.Read(ref versions[componentId]) is { } existing
            ? existing
            : AddType(componentId);
        Interlocked.Increment(ref version.Value);
    }

    internal ComponentWriteVersions CloneDetached()
    {
        ComponentVersion?[] versions = Volatile.Read(ref _versions);
        var clone = new ComponentWriteVersions(new ComponentVersion?[versions.Length]);
        for (int i = 0; i < versions.Length; i++)
        {
            if (versions[i] is { } version)
                clone._versions[i] = new ComponentVersion { Value = Volatile.Read(ref version.Value) };
        }
        return clone;
    }

    private ComponentVersion AddType(int componentId)
    {
        lock (_gate)
        {
            ComponentVersion?[] versions = _versions;
            ArrayGrowthExtensions.EnsureCapacity(ref versions, componentId + 1, 8);
            ComponentVersion version = versions[componentId] ??= new ComponentVersion();
            Volatile.Write(ref _versions, versions);
            return version;
        }
    }

    private sealed class ComponentVersion
    {
        internal int Value;
    }
}
