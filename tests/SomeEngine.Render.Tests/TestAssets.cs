using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Tests;

internal sealed class TestAssets
{
    private readonly object Gate = new();
    private readonly Dictionary<(int Id, int Revision), Mesh> Meshes = [];
    private readonly Dictionary<(int Id, int Revision), Material> Materials = [];
    private readonly Dictionary<(int Id, int Revision), Texture> Textures = [];

    internal Mesh Mesh(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Meshes.TryGetValue((id, revision), out Mesh? asset))
            {
                asset = CreateMesh($"mesh-{id}-r{revision}");
                Meshes.Add((id, revision), asset);
            }
            return asset;
        }
    }

    internal Material Material(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Materials.TryGetValue((id, revision), out Material? asset))
            {
                asset = new StandardMaterial { Name = $"material-{id}-r{revision}" };
                Materials.Add((id, revision), asset);
            }
            return asset;
        }
    }

    internal Texture Texture(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Textures.TryGetValue((id, revision), out Texture? asset))
            {
                asset = SomeEngine.Resources.Texture.Create(new SomeEngine.Graphics.TextureDesc(
                    SomeEngine.Graphics.TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                    SomeEngine.Graphics.Format.R8UNorm, SomeEngine.Graphics.TextureUsages.Sampled,
                    label: $"texture-{id}-r{revision}"),
                    [new TexturePixels(new TextureMipTile { Width = 1, Height = 1 }, new byte[] { 1 })],
                    SomeEngine.Graphics.TextureViewDimension.Texture2D);
                Textures.Add((id, revision), asset);
            }
            return asset;
        }
    }

    private static Mesh CreateMesh(string name)
    {
        const string encoding = "render-tests.opaque";
        int vertices = MeshPageHeader.Size + PackedCluster.SizeInBytes;
        int bvhOffset = vertices + 1 + 3;
        byte[] bytes = new byte[bvhOffset + Unsafe.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1, TotalVertexCount = 1, TotalTriangleCount = 1, VertexBlobBytes = 1,
            ClustersOffset = MeshPageHeader.Size, VertexBlobsOffset = (uint)vertices,
            IndicesOffset = (uint)(vertices + 1), ExtensionDataOffset = (uint)bvhOffset,
            VertexEncodingHash = VertexEncodingCompatibility.Hash(encoding),
        };
        MemoryMarshal.Write(bytes, in header);
        var cluster = new PackedCluster { VertexBlobSize = 1, PackedCounts = 1 | (1u << 8),
            ExtensionOffset = uint.MaxValue, MaterialTableOffset = uint.MaxValue };
        MemoryMarshal.Write(bytes.AsSpan(MeshPageHeader.Size), in cluster);
        var bvh = new ClusterBVHNode();
        var child = new ClusterHierarchySlice { PageRangeKey = 1u, GroupPartData = (1u << 16) | 0xFFFFu };
        child.SetErrors(0, float.MaxValue);
        bvh.SetChild(0, child);
        MemoryMarshal.Write(bytes.AsSpan(bvhOffset), in bvh);
        return SomeEngine.Resources.Mesh.Create(name, encoding, bytes.AsMemory(), (ulong)bvhOffset);
    }
}
