using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Pipeline-independent navigation positions, topology, areas and local bounds.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationGeometryBlock"), BinarySerializable]
[BinaryFields(nameof(Minimum), nameof(Maximum))]
public sealed partial class NavigationGeometryBlock : AssetBlock
{
    public const ulong PositionsChunkKey = 1;
    public const ulong TrianglesChunkKey = 2;
    public partial Vec3 Minimum { get; set; }
    public partial Vec3 Maximum { get; set; }

    public void SetGeometry(NavigationGeometry geometry)
    {
        lock (SyncRoot)
        {
            Minimum = new() { X = geometry.Bounds.Minimum.X, Y = geometry.Bounds.Minimum.Y, Z = geometry.Bounds.Minimum.Z };
            Maximum = new() { X = geometry.Bounds.Maximum.X, Y = geometry.Bounds.Maximum.Y, Z = geometry.Bounds.Maximum.Z };
            SetBytes(PositionsChunkKey, NavigationArrayData.Encode(geometry.Positions));
            SetBytes(TrianglesChunkKey, NavigationArrayData.Encode(geometry.Triangles));
        }
    }

    public async ValueTask<NavigationGeometry> CreateAsync(CancellationToken cancellationToken = default)
    {
        using NavigationGeometryBlock copy = Duplicate();
        ReadOnlyMemory<byte> positions = await copy.ReadBytesAsync(PositionsChunkKey, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> triangles = await copy.ReadBytesAsync(TrianglesChunkKey, cancellationToken).ConfigureAwait(false);
        return new(NavigationArrayData.Decode<Vector3>(positions), NavigationArrayData.Decode<NavigationTriangle>(triangles));
    }
}
