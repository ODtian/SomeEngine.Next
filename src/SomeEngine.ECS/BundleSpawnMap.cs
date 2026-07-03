using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS;

internal sealed class BundleSpawnMap
{
    private readonly int[] _componentColumns;

    internal BundleSpawnMap(ReadOnlySpan<int> sortedComponentIds, Archetype archetype)
    {
        ComponentIds = sortedComponentIds.ToArray();
        Archetype = archetype;

        int maxComponentId = 0;
        for (int i = 0; i < ComponentIds.Length; i++)
            maxComponentId = Math.Max(maxComponentId, ComponentIds[i]);

        _componentColumns = new int[maxComponentId + 1];
        Array.Fill(_componentColumns, -1);

        var columns = archetype.ColumnMetas;
        for (int column = 0; column < columns.Length; column++)
        {
            int componentId = columns[column].ComponentId;
            if ((uint)componentId < (uint)_componentColumns.Length)
                _componentColumns[componentId] = column;
        }
    }

    internal int[] ComponentIds { get; }

    internal Archetype Archetype { get; }

    internal bool HasSharedComponents => Archetype.SharedComponentIds.Length != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Column(int componentId)
    {
        if ((uint)componentId >= (uint)_componentColumns.Length)
            return -1;

        return _componentColumns[componentId];
    }
}

