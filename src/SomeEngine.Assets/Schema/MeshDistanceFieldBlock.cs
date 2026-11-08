using System.Runtime.InteropServices;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources;
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

    public async ValueTask<MeshDistanceField> ReadDataAsync(MeshPayloadSource pages,
        CancellationToken cancellationToken = default)
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
        uint[] materials = new uint[bytes[5].Length / 12];
        int first = 0;
        while (first < materials.Length)
        {
            MeshTrianglePointer pointer = MemoryMarshal.Read<MeshTrianglePointer>(bytes[5].Span.Slice(first * 12));
            ReadOnlyMemory<byte> page = await pages.ReadPageAsync((int)pointer.Page, cancellationToken).ConfigureAwait(false);
            first = ReadPageMaterials(page.Span, MemoryMarshal.Cast<byte, MeshTrianglePointer>(bytes[5].Span),
                first, materials);
        }
        return new(MemoryMarshal.Read<MeshDistanceGrid>(bytes[0].Span),
            MemoryMarshal.Read<MeshTriangleGrid>(bytes[1].Span), bytes[2], bytes[3], bytes[4], bytes[5], materials);
    }

    private static int ReadPageMaterials(ReadOnlySpan<byte> page, ReadOnlySpan<MeshTrianglePointer> pointers,
        int first, Span<uint> materials)
    {
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
        uint pageIndex = pointers[first].Page;
        while (first < pointers.Length && pointers[first].Page == pageIndex)
        {
            uint cluster = pointers[first].Cluster;
            PackedCluster packed = MemoryMarshal.Read<PackedCluster>(page.Slice(
                (int)header.ClustersOffset + (int)cluster * PackedCluster.SizeInBytes));
            int rangeIndex = 0;
            ClusterMaterialRange range = packed.GetMaterialRange(page, rangeIndex);
            while (first < pointers.Length && pointers[first].Page == pageIndex && pointers[first].Cluster == cluster)
            {
                while (pointers[first].Triangle >= range.FirstTriangle + range.TriangleCount)
                    range = packed.GetMaterialRange(page, ++rangeIndex);
                materials[first++] = range.MaterialIndex;
            }
        }
        return first;
    }
}
