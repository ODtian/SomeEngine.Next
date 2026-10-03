using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public readonly ref partial struct QueryChunkView
{
    /// <summary>Returns the unmanaged column bytes and the byte distance between adjacent rows.</summary>
    /// <remarks>All rows receive this query's current write version. Use the span within the query callback.</remarks>
    public unsafe Span<byte> WriteColumn(ushort component, out int stride)
    {
        DevelopmentCheck.Require(!_match.HasRowFilter,
            "Column byte access requires a query with whole-chunk selection.");
        DevelopmentCheck.Require(_match.GetDeclaredAccess(component).CanWrite(),
            "Column byte access requires a declared query write term for the component.");
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(component);
        ComponentByteChecks.Require(in info);
        DevelopmentCheck.Require(info.Storage == StoragePath.Table,
            "Column byte access requires a table component.");
        int column = _match.Archetype.Column(component);
        stride = info.Size;
        _world.Components.WriteChunk(_chunk, column, component, CurrentSystemVersion);
        ref byte bytes = ref _chunk.ComponentRowReference(column, 0, in info.Operations);
        return MemoryMarshal.CreateSpan(ref bytes, _chunk.Count * stride);
    }
}
