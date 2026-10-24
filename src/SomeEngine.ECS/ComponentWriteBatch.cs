using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

/// <summary>Current component roots borrowed with one write permission and version for a batch.</summary>
public readonly ref struct ComponentWriteBatch<TComponent> where TComponent : struct, IComponent
{
    private readonly World _world;
    private readonly bool _sparse;

    internal ComponentWriteBatch(World world, bool sparse, uint version) =>
        (_world, _sparse, CurrentSystemVersion) = (world, sparse, version);

    public uint CurrentSystemVersion { get; }

    /// <summary>Resolves the current row and advances its component version before returning its root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TComponent Write(Entity entity)
    {
        if (_sparse)
            return ref _world.Sparse.WriteRef<TComponent>(entity, CurrentSystemVersion);
        return ref _world.Components.WriteRef<TComponent>(entity, CurrentSystemVersion);
    }
}
