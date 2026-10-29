using System.Runtime.InteropServices;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>Mesh SDF samples and triangle pointers stored with their mesh asset.</summary>
[AssetBlock("SomeEngine.Assets.Schema.MeshDistanceFieldBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity))]
public sealed partial class MeshDistanceFieldBlock : AssetBlock
{
    private static readonly ulong GridKey = BinaryFieldKey.FromName("MeshDistanceField.Grid");
    private static readonly ulong TriangleGridKey = BinaryFieldKey.FromName("MeshDistanceField.TriangleGrid");
    private static readonly ulong DistancesKey = BinaryFieldKey.FromName("MeshDistanceField.Distances");
    private static readonly ulong RangesKey = BinaryFieldKey.FromName("MeshDistanceField.CellRanges");
    private static readonly ulong ReferencesKey = BinaryFieldKey.FromName("MeshDistanceField.TriangleReferences");
    private static readonly ulong PointersKey = BinaryFieldKey.FromName("MeshDistanceField.TrianglePointers");

    public partial Guid VertexIdentity { get; set; }

    public static MeshDistanceFieldBlock Build(ClusterGeometryBlock geometry,
        ClusterVertexAttributesBlock attributes, int distanceBudgetPerTriangle, int triangleGridSide,
        CancellationToken cancellationToken = default) =>
        MeshDistanceFieldBuilder.Build(geometry, attributes, distanceBudgetPerTriangle, triangleGridSide, cancellationToken);

    public void SetData(Guid vertexIdentity, MeshDistanceField data)
    {
        MeshDistanceGrid grid = data.Grid;
        MeshTriangleGrid triangleGrid = data.TriangleGrid;
        lock (SyncRoot)
        {
            VertexIdentity = vertexIdentity;
            SetBytes(GridKey, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref grid, 1)).ToArray());
            SetBytes(TriangleGridKey, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref triangleGrid, 1)).ToArray());
            SetBytes(DistancesKey, data.Distances);
            SetBytes(RangesKey, data.CellRanges);
            SetBytes(ReferencesKey, data.TriangleReferences);
            SetBytes(PointersKey, data.TrianglePointers);
        }
    }

    public async ValueTask<MeshDistanceField> ReadDataAsync(CancellationToken cancellationToken = default)
    {
        using MeshDistanceFieldBlock copy = Duplicate();
        Task<ReadOnlyMemory<byte>>[] reads = [
            copy.ReadBytesAsync(GridKey, cancellationToken).AsTask(),
            copy.ReadBytesAsync(TriangleGridKey, cancellationToken).AsTask(),
            copy.ReadBytesAsync(DistancesKey, cancellationToken).AsTask(),
            copy.ReadBytesAsync(RangesKey, cancellationToken).AsTask(),
            copy.ReadBytesAsync(ReferencesKey, cancellationToken).AsTask(),
            copy.ReadBytesAsync(PointersKey, cancellationToken).AsTask(),
        ];
        ReadOnlyMemory<byte>[] bytes = await Task.WhenAll(reads).ConfigureAwait(false);
        return new(MemoryMarshal.Read<MeshDistanceGrid>(bytes[0].Span),
            MemoryMarshal.Read<MeshTriangleGrid>(bytes[1].Span), bytes[2], bytes[3], bytes[4], bytes[5]);
    }
}
