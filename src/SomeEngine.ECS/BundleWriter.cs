using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS;

internal enum BundleWriteMode : byte
{
    Spawn,
    Add,
    Replace,
}

/// <summary>
/// Write context consumed by generated bundle methods.
/// </summary>
public readonly struct BundleWriter
{
    private readonly Owners.Bundles _bundles;
    private readonly Archetype? _sourceArchetype;
    private readonly Archetype _archetype;
    private readonly BundleSpawnMap? _spawnMap;
    private readonly Chunk _chunk;
    private readonly int _row;
    private readonly BundleWriteMode _mode;

    internal BundleWriter(
        Owners.Bundles bundles,
        Entity entity,
        Archetype? sourceArchetype,
        Archetype archetype,
        BundleSpawnMap? spawnMap,
        Chunk chunk,
        int row,
        BundleWriteMode mode
    )
    {
        _bundles = bundles;
        Entity = entity;
        _sourceArchetype = sourceArchetype;
        _archetype = archetype;
        _spawnMap = spawnMap;
        _chunk = chunk;
        _row = row;
        _mode = mode;
    }

    public Entity Entity { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in T value)
        where T : struct, IComponent
    {
        if (_spawnMap is not null)
        {
            _bundles.WriteSpawn(Entity, _spawnMap, _chunk, _row, value);
            return;
        }

        _bundles.WriteEntity(Entity, _sourceArchetype, _archetype, _chunk, _row, value, _mode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSparse<T>(in T value)
        where T : struct, ISparseComponent
    {
        _bundles.WriteSparse(Entity, value, _mode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBuffer<T>(in BufferValues<T> value)
        where T : struct, IBufferElement
    {
        _bundles.WriteBuffer(Entity, value.AsSpan(), _mode);
    }
}

