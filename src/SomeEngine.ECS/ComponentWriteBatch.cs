using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS;

/// <summary>Current component roots borrowed with one write permission and version for a batch.</summary>
public readonly ref struct ComponentWriteBatch<TComponent> where TComponent : struct, IComponent
{
    private readonly World _world;
    private readonly bool _sparse;

    internal ComponentWriteBatch(World world, bool sparse, uint version) =>
        (_world, _sparse, CurrentSystemVersion) = (world, sparse, version);

    public uint CurrentSystemVersion { get; }

    internal World World => _world;
    internal WorldStructureRoot Structure => _world.ActiveStructureRoot;
    internal bool IsSparse => _sparse;

    /// <summary>Borrows a declared query column using this batch's shared write version.</summary>
    public ComponentWriteColumn<TComponent> ReadWriteColumn(QueryChunkView chunk) =>
        chunk.ReadWriteColumn<TComponent>(_world, CurrentSystemVersion);

    /// <summary>Resolves the current row and advances its component version before returning its root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TComponent Write(Entity entity)
    {
        if (_sparse)
            return ref _world.Sparse.WriteRef<TComponent>(entity, CurrentSystemVersion);
        return ref _world.Components.WriteRef<TComponent>(entity, CurrentSystemVersion);
    }
}
