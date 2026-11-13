using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>Checks view and callback reads against the World's component type versions.</summary>
internal sealed class DependencyChanges
{
    private readonly List<int> _types = new();
    private readonly List<Dependency> _dependencies = new();
    private ComponentChange[] _changes = new ComponentChange[16];

    internal ulong Frame { get; private set; }

    internal void Clear()
    {
        _types.Clear();
        _dependencies.Clear();
        Array.Clear(_changes);
    }

    internal void Record(World world, ulong frame)
    {
        bool sameFrame = Frame == frame;
        Frame = frame;
        foreach (int component in _types)
        {
            ref ComponentChange change = ref _changes[component];
            ulong version = world.ComponentWriteVersion(component);
            change.Changed = version != change.Version || (sameFrame && change.Changed);
            change.Version = version;
        }
    }

    private bool HasComponentChanges(World world, int component, bool recheck)
    {
        if (component >= _changes.Length)
            Array.Resize(ref _changes, Math.Max(component + 1, _changes.Length * 2));
        ref ComponentChange change = ref _changes[component];
        if (!change.Recorded)
        {
            change.Version = world.ComponentWriteVersion(component);
            change.Changed = true;
            change.Recorded = true;
            _types.Add(component);
        }
        return change.Changed || (recheck && world.ComponentWriteVersion(component) != change.Version);
    }

    internal bool HasChanges(World world, Entity entity, Dependency read, bool recheck) => read.Kind switch
    {
        DependencyKind.Environment =>
            (HasComponentChanges(world, read.Component, recheck) ||
                HasComponentChanges(world, ComponentId<Parent<ElementTree>>.Value, recheck)) &&
            EnvironmentLookup.HasChanges(world, entity, in read),
        DependencyKind.Component => HasComponentChanges(world, read.Component, recheck) &&
            HasChanges(world, entity, read),
        _ => HasChanges(world, entity, read),
    };

    internal static bool HasChanges(World world, Entity entity, Dependency read) => read.Kind switch
    {
        DependencyKind.Environment => EnvironmentLookup.HasChanges(world, entity, in read),
        DependencyKind.Presence => world.Has(read.Entity, read.Component) != (read.Version != 0),
        DependencyKind.Membership => read.Query.MembershipVersion != read.Version,
        _ => world.ComponentWriteVersion(read.Entity, read.Component) != read.Version,
    };

    internal bool HasChanges(World world, Entity entity, bool recheck)
    {
        if (world.Has<ObjectDependencies>(entity) && world.Read<ObjectDependencies>(entity).Reads.HasChanges(Frame))
            return true;
        if (!world.Has<Buffer<Dependency>>(entity))
            return false;

        _dependencies.Clear();
        List<Dependency> dependencies = _dependencies;
        {
            using var buffer = world.ReadBuffer<Dependency>(entity);
            foreach (Dependency read in buffer.AsSpan())
                dependencies.Add(read);
        }
        foreach (Dependency read in _dependencies)
            if (HasChanges(world, entity, read, recheck))
                return true;
        return false;
    }

    internal void CopyVersions(World world, Entity entity, List<DependencyVersion> versions)
    {
        if (world.Has<ObjectDependencies>(entity))
            world.Read<ObjectDependencies>(entity).Reads.CopyVersions(versions, Frame);
        if (!world.Has<Buffer<Dependency>>(entity)) return;
        _dependencies.Clear();
        List<Dependency> dependencies = _dependencies;
        {
            using var buffer = world.ReadBuffer<Dependency>(entity);
            foreach (Dependency read in buffer.AsSpan()) dependencies.Add(read);
        }
        CopyVersions(world, entity, CollectionsMarshal.AsSpan(_dependencies), versions);
    }

    internal void CopyVersions(World world, Entity entity, ReadOnlySpan<Dependency> reads,
        List<DependencyVersion> versions)
    {
        foreach (Dependency read in reads)
        {
            switch (read.Kind)
            {
                case DependencyKind.Environment:
                    if (!HasComponentChanges(world, read.Component, recheck: false) &&
                        !HasComponentChanges(world, ComponentId<Parent<ElementTree>>.Value, recheck: false)) break;
                    Entity source = EnvironmentLookup.FindSource(world, entity, read.Component);
                    ulong? version = source == Entity.Null ? 0 : world.ComponentWriteVersion(source, read.Component);
                    versions.Add(new(read.Version, version, source != read.Entity));
                    break;
                case DependencyKind.Presence:
                    versions.Add(new(read.Version, world.Has(read.Entity, read.Component) ? 1u : 0u));
                    break;
                case DependencyKind.Membership:
                    versions.Add(new(read.Version, read.Query.MembershipVersion));
                    break;
                default:
                    if (HasComponentChanges(world, read.Component, recheck: false))
                        versions.Add(new(read.Version, world.ComponentWriteVersion(read.Entity, read.Component)));
                    break;
            }
        }
    }

    private struct ComponentChange
    {
        internal ulong Version;
        internal bool Changed;
        internal bool Recorded;
    }
}

internal readonly record struct DependencyVersion(ulong ReadVersion, ulong? CurrentVersion, bool Changed = false)
{
    internal bool HasChanges => Changed || ReadVersion != CurrentVersion;
}
