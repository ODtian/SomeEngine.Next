using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace DefaultGame;

internal readonly record struct PhysicsSkinAttributes(StandardClusterAttributes Surface, Vector4 Joints, Vector4 Weights);

/// <summary>The example's standard surface followed by four joint indices and four byte weights.</summary>
internal sealed class PhysicsSkinVertexEncoder : IClusterVertexEncoder<PhysicsSkinAttributes>
{
    internal static readonly PhysicsSkinVertexEncoder Instance = new();
    public string Compatibility => "someengine.cluster.positions.uint16.v1+" + CrowdExample.VertexEncoding;
    public IClusterVertexEncoder<PhysicsSkinAttributes> CreateBatchEncoder() => this;

    internal static (Mesh Mesh, ClusterSkinBounds Bounds) Build(Vector3[] positions,
        PhysicsSkinAttributes[] attributes, uint[] indices, string name)
    {
        Mesh geometry = ClusterMeshBuilder.Build(positions, attributes, indices,
            [new ClusterMaterialGroup(0, indices.Length, 0)], Instance, ClusterBuildStrategy.Realtime, name);
        var pages = new ReadOnlyMemory<byte>[geometry.PageCount];
        var clusters = new List<SkinJointBounds[]>();
        var lod = new List<SkinClusterLod>();
        for (int page = 0; page < pages.Length; page++)
        {
            geometry.TryGetPage(page, out pages[page]);
            ReadOnlySpan<byte> bytes = pages[page].Span;
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(bytes);
            for (int cluster = 0; cluster < header.ClusterCount; cluster++)
            {
                PackedCluster description = MemoryMarshal.Read<PackedCluster>(bytes[(MeshPageHeader.Size + cluster * PackedCluster.SizeInBytes)..]);
                ReadOnlySpan<byte> vertices = bytes.Slice((int)(header.VertexBlobsOffset + description.VertexBlobOffset), (int)description.VertexBlobSize);
                int count = (int)(description.PackedCounts & 255);
                int skin = vertices.Length - count * 12 - 4;
                Vector3 minimum = new(BinaryPrimitives.ReadSingleLittleEndian(vertices),
                    BinaryPrimitives.ReadSingleLittleEndian(vertices[4..]), BinaryPrimitives.ReadSingleLittleEndian(vertices[8..]));
                float step = BinaryPrimitives.ReadSingleLittleEndian(vertices[12..]);
                var bounds = new Dictionary<uint, (Vector3 Minimum, Vector3 Maximum)>();
                for (int vertex = 0; vertex < count; vertex++)
                {
                    ReadOnlySpan<byte> position = vertices[(16 + vertex * 6)..];
                    Vector3 value = minimum + step * new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(position),
                        BinaryPrimitives.ReadUInt16LittleEndian(position[2..]), BinaryPrimitives.ReadUInt16LittleEndian(position[4..]));
                    ReadOnlySpan<byte> influences = vertices[(skin + vertex * 12)..];
                    for (int influence = 0; influence < 4; influence++)
                    {
                        if (influences[8 + influence] == 0) continue;
                        uint joint = BinaryPrimitives.ReadUInt16LittleEndian(influences[(influence * 2)..]);
                        bounds[joint] = bounds.TryGetValue(joint, out var box)
                            ? (Vector3.Min(box.Minimum, value), Vector3.Max(box.Maximum, value)) : (value, value);
                    }
                }
                clusters.Add(bounds.OrderBy(pair => pair.Key).Select(pair => new SkinJointBounds(pair.Key,
                    pair.Value.Minimum, pair.Value.Maximum)).ToArray());
                lod.Add(new(description.GroupId, -1, 0, 0, step));
            }
        }
        Mesh mesh = Mesh.Create(name, geometry.VertexEncoding, geometry.PageDigests, geometry.Bvh, pages,
            geometry.Bounds, geometry.Regions, geometry.LowestDetailPageIndices,
            Enumerable.Range(0, clusters.Count).Select(index => (uint)index).ToArray());
        return (mesh, new(clusters, lod));
    }

    public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<PhysicsSkinAttributes> attributes,
        ReadOnlySpan<uint> clusterVertices)
    {
        int count = clusterVertices.Length;
        var localPositions = new Vector3[count];
        var surfaces = new StandardClusterAttributes[count];
        var localVertices = new uint[count];
        for (int index = 0; index < count; index++)
        {
            int source = (int)clusterVertices[index];
            localPositions[index] = positions[source];
            surfaces[index] = attributes[source].Surface;
            localVertices[index] = (uint)index;
        }
        EncodedClusterVertices surface = StandardClusterVertexEncoder.Instance.Encode(localPositions, surfaces, localVertices);
        var bytes = new byte[surface.Blob.Length + count * 12 + 4];
        surface.Blob.Span.CopyTo(bytes);
        for (int index = 0; index < count; index++)
        {
            PhysicsSkinAttributes value = attributes[(int)clusterVertices[index]];
            Span<byte> row = bytes.AsSpan(surface.Blob.Length + index * 12, 12);
            for (int influence = 0; influence < 4; influence++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(row[(influence * 2)..], (ushort)value.Joints[influence]);
                row[8 + influence] = (byte)MathF.Round(value.Weights[influence] * 255);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), (uint)(surface.Blob.Length - surface.PositionBytes));
        return surface with { Blob = bytes };
    }
}
