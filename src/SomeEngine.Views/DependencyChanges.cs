using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>Checks view and callback reads against the World's component type versions.</summary>
internal sealed class DependencyChanges
{
    private readonly List<ushort> _types = new();
    private ComponentChange[] _changes = new ComponentChange[16];

    internal ulong Frame { get; private set; }

    internal void Clear()
    {
        _types.Clear();
        Array.Clear(_changes);
    }

    internal void Record(World world, ulong frame)
    {
        bool sameFrame = Frame == frame;
        Frame = frame;
        foreach (ushort component in _types)
        {
            ref ComponentChange change = ref _changes[component];
            uint version = world.ComponentWriteVersion(component);
            change.Changed = version != change.Version || (sameFrame && change.Changed);
            change.Version = version;
        }
    }

    private bool HasComponentChanges(World world, ushort component, bool recheck)
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
        DependencyKind.Presence => world.HasComponentData(read.Entity, read.Component) != (read.Version != 0),
        DependencyKind.Membership => world.MembershipVersion(read.Query) != read.Version,
        _ => world.ComponentDataWriteVersion(read.Entity, read.Component) != read.Version,
    };

    internal bool HasChanges(World world, Entity entity, bool recheck)
    {
        if (world.Has<ObjectDependencies>(entity) && world.Read<ObjectDependencies>(entity).Reads.HasChanges(Frame))
            return true;
        if (!world.HasBuffer<Dependency>(entity))
            return false;

        var changes = (Checks: this, World: world, Entity: entity, Recheck: recheck, Found: false);
        world.ExecuteBufferRead<Dependency,
            (DependencyChanges Checks, World World, Entity Entity, bool Recheck, bool Found)>(
            entity, ref changes, static (buffer, ref result) =>
        {
            foreach (Dependency read in buffer.AsSpan())
            {
                if (result.Checks.HasChanges(result.World, result.Entity, read, result.Recheck))
                {
                    result.Found = true;
                    return;
                }
            }
        });
        return changes.Found;
    }

    private struct ComponentChange
    {
        internal uint Version;
        internal bool Changed;
        internal bool Recorded;
    }
}
