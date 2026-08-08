using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

internal sealed class BundleSpawnMap
{
    private readonly int[] _componentIds;
    private readonly int[] _componentColumns;
    private readonly int[] _descriptorIndices;

    internal BundleSpawnMap(ReadOnlySpan<int> sortedComponentIds, Archetype archetype)
    {
        _componentIds = sortedComponentIds.ToArray();
        Archetype = archetype;

        int maxComponentId = 0;
        for (int i = 0; i < _componentIds.Length; i++)
            maxComponentId = Math.Max(maxComponentId, _componentIds[i]);

        _componentColumns = new int[maxComponentId + 1];
        Array.Fill(_componentColumns, -1);
        _descriptorIndices = new int[maxComponentId + 1];
        Array.Fill(_descriptorIndices, -1);

        UInt128 requiredWrites = 0;
        for (int i = 0; i < _componentIds.Length; i++)
        {
            _descriptorIndices[_componentIds[i]] = i;
            if (i < 128 && ComponentRegistry.Get(_componentIds[i]).Storage != StoragePath.Tag)
                requiredWrites |= (UInt128)1 << i;
        }
        RequiredWrites = requiredWrites;

        ReadOnlySpan<int> columns = archetype.TableComponentIds;
        for (int column = 0; column < columns.Length; column++)
        {
            int componentId = columns[column];
            if ((uint)componentId < (uint)_componentColumns.Length)
                _componentColumns[componentId] = column;
        }
    }

    internal ReadOnlySpan<int> ComponentIds => _componentIds;

    internal Archetype Archetype { get; }

    internal bool HasSharedComponents => Archetype.SharedComponentIds.Length != 0;

    internal UInt128 RequiredWrites { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int DescriptorIndex(int componentId)
    {
        if ((uint)componentId >= (uint)_descriptorIndices.Length)
            return -1;

        return _descriptorIndices[componentId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Column(int componentId)
    {
        if ((uint)componentId >= (uint)_componentColumns.Length)
            return -1;

        return _componentColumns[componentId];
    }
}

