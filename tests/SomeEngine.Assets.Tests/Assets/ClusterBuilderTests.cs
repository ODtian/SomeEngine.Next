using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public class ClusterBuilderTests
{
    [Fact]
    public void TestClusterGeneration()
    {
        // 1. Create a 32x32 plane
        int w = 32;
        int h = 32;
        var positions = new Vector3[w * h];
        var normals = new float[w * h * 3];
        var uvs = new float[w * h * 2];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                positions[y * w + x] = new Vector3(x, 0, y);
                normals[(y * w + x) * 3 + 0] = 0;
                normals[(y * w + x) * 3 + 1] = 1;
                normals[(y * w + x) * 3 + 2] = 0;
                uvs[(y * w + x) * 2 + 0] = x / (float)w;
                uvs[(y * w + x) * 2 + 1] = y / (float)h;
            }
        }

        var indicesList = new List<uint>();
        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                uint i0 = (uint)(y * w + x);
                uint i1 = (uint)(y * w + x + 1);
                uint i2 = (uint)((y + 1) * w + x);
                uint i3 = (uint)((y + 1) * w + x + 1);

                indicesList.Add(i0);
                indicesList.Add(i2);
                indicesList.Add(i1);

                indicesList.Add(i1);
                indicesList.Add(i2);
                indicesList.Add(i3);
            }
        }

        var indices = indicesList.ToArray();

        var rawAttributes = new List<RawAttribute>
        {
            new RawAttribute("NORMAL", normals, 3, ValueType.Int8, 3, true),
            new RawAttribute("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false)
        };

        // 2. Run Builder
        var asset = ClusterBuilder.ProcessRaw(
            positions,
            rawAttributes,
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "TestPlane");

        // 3. Assertions
        Assert.NotNull(asset.Payload);
        Assert.True(asset.Payload.Value.Length > 0);

        // Check if we have at least one page
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        Assert.True(header.ClusterCount > 0);
        Assert.True(header.TotalVertexCount > 0);
    }

    [Fact]
    public void StandardEncoder_WritesOneOpaqueClusterLocalBitstream()
    {
        // Create a minimal triangle with known attribute values
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 }; // All pointing up
        var uvs = new float[] { 0, 0, 1, 0, 0, 1 }; // Standard triangle UVs
        var indices = new uint[] { 0, 1, 2 };

        var rawAttributes = new List<RawAttribute>
        {
            new("NORMAL", normals, 3, ValueType.Int8, 3, true),       // 3 bytes/vertex
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),   // 4 bytes/vertex
        };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            rawAttributes,
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "TestSoA");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));

        Assert.True(header.ClusterCount > 0);
        uint totalVerts = header.TotalVertexCount;
        Assert.Equal(3u, totalVerts);

        Assert.Equal(StandardClusterVertexEncoder.EncodingCompatibility, asset.VertexEncoding);
        Assert.Equal(
            VertexEncodingCompatibility.Hash(StandardClusterVertexEncoder.EncodingCompatibility),
            header.VertexEncodingHash);
        Assert.Equal(
            header.VertexBlobsOffset + header.VertexBlobBytes,
            header.IndicesOffset);

        List<DecodedStandardVertex> decoded = DecodeStandardVertices(span, header);
        Assert.Equal(3, decoded.Count);
        Assert.All(decoded, static vertex => Assert.Equal(Vector3.UnitY, vertex.Normal));
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(0, 0)) < 0.01f);
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(1, 0)) < 0.01f);
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(0, 1)) < 0.01f);
    }

    [Fact]
    public void StandardEncoder_PacksTangentAndVariablePrecisionUvInTheOpaqueBlob()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 };
        var tangents = new float[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1 };
        var uvs = new float[] { 0, 0, 1, 0, 0, 1 };
        var indices = new uint[] { 0, 1, 2 };

        var rawAttributes = new List<RawAttribute>
        {
            new("NORMAL", normals, 3, ValueType.Int8, 3, true),
            new("TANGENT", tangents, 4, ValueType.Int8, 4, true),
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),
        };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            rawAttributes,
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "TestSoAWithTangent");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));

        List<DecodedStandardVertex> decoded = DecodeStandardVertices(span, header);
        Assert.Equal(3, decoded.Count);
        Assert.All(
            decoded,
            static vertex => Assert.Equal(new Vector4(1, 0, 0, 1), vertex.Tangent));
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(0, 0)) < 0.01f);
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(1, 0)) < 0.01f);
        Assert.Contains(decoded, static vertex => Vector2.Distance(vertex.Uv, new Vector2(0, 1)) < 0.01f);
        Assert.Equal(header.VertexBlobsOffset + header.VertexBlobBytes, header.IndicesOffset);
    }

    [Fact]
    public void CustomEncoder_OwnsBlobBytesWithoutChangingTheClusterPageProtocol()
    {
        var positions = new Vector3[]
        {
            new(-2, 1, 0),
            new(3, -4, 2),
            new(0, 5, -1),
        };
        var encoder = new SentinelVertexEncoder();

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            [],
            [0, 1, 2],
            ClusterBuilderTestMaterials.Slots(),
            "CustomVertexEncoding",
            encoder);

        Assert.Equal(SentinelVertexEncoder.CompatibilityName, asset.VertexEncoding);
        ReadOnlySpan<byte> payload = asset.Payload!.Value.Span;
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(payload);
        Assert.Equal(
            VertexEncodingCompatibility.Hash(SentinelVertexEncoder.CompatibilityName),
            header.VertexEncodingHash);
        Assert.Equal(header.VertexBlobsOffset + header.VertexBlobBytes, header.IndicesOffset);

        ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            payload.Slice(
                checked((int)header.ClustersOffset),
                checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
        Assert.False(clusters.IsEmpty);
        foreach (ref readonly PackedCluster cluster in clusters)
        {
            ReadOnlySpan<byte> blob = payload.Slice(
                checked((int)(header.VertexBlobsOffset + cluster.VertexBlobOffset)),
                checked((int)cluster.VertexBlobSize));
            Assert.Equal(2, blob.Length);
            Assert.Equal(0xD7, blob[0]);
            Assert.Equal(checked((byte)(cluster.PackedCounts & 0xFF)), blob[1]);
        }
    }

    [Fact]
    public void ProcessRaw_DoesNotMergeVerticesWithDifferentAttributes()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 0),
        };
        var uvs = new float[]
        {
            0, 0,
            1, 0,
            0, 1,
            0.5f, 0.5f,
        };
        var indices = new uint[] { 0, 1, 2, 3, 2, 1 };
        var rawAttributes = new List<RawAttribute>
        {
            new("TEXCOORD_0", uvs, 2, ValueType.Float16, 2, false),
        };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            rawAttributes,
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "UvSeam");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        Assert.Equal(4u, header.TotalVertexCount);
        Assert.Contains(
            DecodeStandardVertices(span, header),
            static vertex => Math.Abs(vertex.Uv.X - 0.5f) < 0.01f &&
                Math.Abs(vertex.Uv.Y - 0.5f) < 0.01f);
    }

    [Fact]
    public void ProcessRaw_ClusterBoundsContainDecodedQuantizedPositions()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1.00002f, 0, 0),
            new(0, 1.00002f, 0),
        };
        var indices = new uint[] { 0, 1, 2 };

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            new List<RawAttribute>(),
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "QuantizedBounds");

        Assert.NotNull(asset.Payload);
        var span = asset.Payload.Value.Span;
        var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(0, MeshPageHeader.Size));
        var clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            span.Slice(
                (int)header.ClustersOffset,
                checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));

        foreach (ref readonly var cluster in clusters)
        {
            uint vertexCount = cluster.PackedCounts & 0xFF;
            for (uint local = 0; local < vertexCount; local++)
            {
                Vector3 decoded = DecodeStandardVertex(span, header, cluster, local).Position;

                Assert.InRange(decoded.X, cluster.BoundMin.X, cluster.BoundMax.X);
                Assert.InRange(decoded.Y, cluster.BoundMin.Y, cluster.BoundMax.Y);
                Assert.InRange(decoded.Z, cluster.BoundMin.Z, cluster.BoundMax.Z);
            }
        }
    }

    [Fact]
    public void ProcessRaw_WritesMeshRegions_FromSourceSlots()
    {
        var positions = new Vector3[]
        {
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
        };
        var indices = new uint[] { 0, 1, 2 };
        var materialGuid = SomeEngine.Assets.AssetGuid.New();
        var materialSlot = new MeshMaterialSlot(
            materialGuid,
            ClusterBuilderTestMaterials.Slot(0).DeformCompatibility);

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            new List<RawAttribute>(),
            indices,
            new List<MeshMaterialSlot> { materialSlot },
            "GuidMesh");

        Assert.NotNull(asset.Regions);
        Assert.Single(asset.Regions);
        Assert.Equal("region_0", asset.Regions[0].Name);
    }

    [Fact]
    public void ProcessRaw_PartitionsEveryClusterByDeformCompatibility()
    {
        Vector3[] positions =
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
            new(10, 0, 0),
            new(11, 0, 0),
            new(10, 1, 0),
        ];
        uint[] indices = [0, 1, 2, 3, 4, 5];
        var materialIndices = new RawAttribute(
            "_MATERIAL_INDEX",
            [0, 0, 0, 1, 1, 1],
            1,
            ValueType.UInt8,
            1,
            false);
        MeshMaterialSlot[] materials =
        [
            ClusterBuilderTestMaterials.Slot(0),
            ClusterBuilderTestMaterials.Slot(1),
        ];

        var asset = ClusterBuilder.ProcessRaw(
            positions,
            [materialIndices],
            indices,
            materials,
            "MixedDeform");

        ReadOnlySpan<byte> payload = asset.Payload!.Value.Span;
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(payload);
        ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            payload.Slice(
                checked((int)header.ClustersOffset),
                checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
        var explicitDeformMaterials = new HashSet<byte>();
        foreach (ref readonly PackedCluster cluster in clusters)
        {
            byte deformMaterial = checked((byte)(cluster.PackedCounts >> 24));
            explicitDeformMaterials.Add(deformMaterial);
            byte material0 = checked((byte)cluster.PackedMaterials);
            Assert.Equal(
                materials[deformMaterial].DeformCompatibility,
                materials[material0].DeformCompatibility);

            uint triangleCount = (cluster.PackedCounts >> 8) & 0xFF;
            uint range0End = cluster.PackedRanges & 0xFF;
            uint range1End = (cluster.PackedRanges >> 8) & 0xFF;
            if (range0End < triangleCount)
            {
                byte material1 = checked((byte)(cluster.PackedMaterials >> 8));
                Assert.Equal(
                    materials[deformMaterial].DeformCompatibility,
                    materials[material1].DeformCompatibility);
            }
            if (range1End < triangleCount)
            {
                byte material2 = checked((byte)(cluster.PackedMaterials >> 16));
                Assert.Equal(
                    materials[deformMaterial].DeformCompatibility,
                    materials[material2].DeformCompatibility);
            }
        }

        Assert.Equal(new byte[] { 0, 1 }, explicitDeformMaterials.Order().ToArray());
    }

    private static List<DecodedStandardVertex> DecodeStandardVertices(
        ReadOnlySpan<byte> payload,
        in MeshPageHeader header)
    {
        ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            payload.Slice(
                checked((int)header.ClustersOffset),
                checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
        var values = new List<DecodedStandardVertex>(checked((int)header.TotalVertexCount));
        foreach (ref readonly PackedCluster cluster in clusters)
        {
            uint vertexCount = cluster.PackedCounts & 0xFFu;
            for (uint vertex = 0; vertex < vertexCount; vertex++)
                values.Add(DecodeStandardVertex(payload, header, cluster, vertex));
        }
        return values;
    }

    private static DecodedStandardVertex DecodeStandardVertex(
        ReadOnlySpan<byte> payload,
        in MeshPageHeader header,
        in PackedCluster cluster,
        uint localVertex)
    {
        int blob = checked((int)(header.VertexBlobsOffset + cluster.VertexBlobOffset));
        Assert.Equal(StandardClusterVertexEncoder.BlobMagic, BitConverter.ToUInt32(payload.Slice(blob, 4)));
        Assert.Equal(cluster.VertexBlobSize, BitConverter.ToUInt32(payload.Slice(blob + 4, 4)));
        Assert.True(localVertex < BitConverter.ToUInt32(payload.Slice(blob + 8, 4)));

        uint packedBits = BitConverter.ToUInt32(payload.Slice(blob + 12, 4));
        int positionBitsX = checked((int)(packedBits & 31u));
        int positionBitsY = checked((int)((packedBits >> 5) & 31u));
        int positionBitsZ = checked((int)((packedBits >> 10) & 31u));
        int uvBitsX = checked((int)((packedBits >> 15) & 31u));
        int uvBitsY = checked((int)((packedBits >> 20) & 31u));
        uint recordBits = BitConverter.ToUInt32(payload.Slice(blob + 16, 4));
        uint bit = checked(
            BitConverter.ToUInt32(payload.Slice(blob + 20, 4)) + localVertex * recordBits);

        uint x = ReadBits(payload, blob, ref bit, positionBitsX);
        uint y = ReadBits(payload, blob, ref bit, positionBitsY);
        uint z = ReadBits(payload, blob, ref bit, positionBitsZ);
        uint packedNormal = ReadBits(payload, blob, ref bit, 24);
        uint packedTangent = ReadBits(payload, blob, ref bit, 32);
        uint u = ReadBits(payload, blob, ref bit, uvBitsX);
        uint v = ReadBits(payload, blob, ref bit, uvBitsY);

        Vector3 positionOrigin = new(
            BitConverter.ToSingle(payload.Slice(blob + 24, 4)),
            BitConverter.ToSingle(payload.Slice(blob + 28, 4)),
            BitConverter.ToSingle(payload.Slice(blob + 32, 4)));
        float positionStep = BitConverter.ToSingle(payload.Slice(blob + 36, 4));
        Vector2 uvOrigin = new(
            BitConverter.ToSingle(payload.Slice(blob + 40, 4)),
            BitConverter.ToSingle(payload.Slice(blob + 44, 4)));
        float uvStep = BitConverter.ToSingle(payload.Slice(blob + 48, 4));
        return new DecodedStandardVertex(
            positionOrigin + new Vector3(x, y, z) * positionStep,
            DecodeSnorm3(packedNormal),
            DecodeSnorm4(packedTangent),
            uvOrigin + new Vector2(u, v) * uvStep);
    }

    private static uint ReadBits(
        ReadOnlySpan<byte> payload,
        int blob,
        ref uint bit,
        int count)
    {
        uint value = 0;
        for (int index = 0; index < count; index++, bit++)
        {
            uint source = payload[checked(blob + (int)(bit >> 3))];
            value |= ((source >> checked((int)(bit & 7u))) & 1u) << index;
        }
        return value;
    }

    private static Vector3 DecodeSnorm3(uint value) => new(
        DecodeSnorm8(value),
        DecodeSnorm8(value >> 8),
        DecodeSnorm8(value >> 16));

    private static Vector4 DecodeSnorm4(uint value) => new(
        DecodeSnorm8(value),
        DecodeSnorm8(value >> 8),
        DecodeSnorm8(value >> 16),
        DecodeSnorm8(value >> 24));

    private static float DecodeSnorm8(uint value)
        => Math.Max(unchecked((sbyte)(byte)value) / 127.0f, -1.0f);

    private sealed class SentinelVertexEncoder : IClusterVertexEncoder
    {
        internal const string CompatibilityName = "test.vertex.sentinel.v1";

        public string Compatibility => CompatibilityName;

        public EncodedClusterVertices Encode(
            ReadOnlySpan<Vector3> positions,
            IReadOnlyList<RawAttribute> attributes,
            ReadOnlySpan<uint> clusterVertices)
        {
            Assert.Empty(attributes);
            Assert.False(clusterVertices.IsEmpty);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            foreach (uint sourceVertex in clusterVertices)
            {
                Vector3 position = positions[checked((int)sourceVertex)];
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            return new EncodedClusterVertices(
                new byte[] { 0xD7, checked((byte)clusterVertices.Length) },
                min,
                max);
        }
    }

    private readonly record struct DecodedStandardVertex(
        Vector3 Position,
        Vector3 Normal,
        Vector4 Tangent,
        Vector2 Uv);

}
