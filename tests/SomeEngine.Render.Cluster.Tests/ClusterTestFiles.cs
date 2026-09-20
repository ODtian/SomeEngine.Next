using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Containers;
using SomeEngine.Assets.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ClusterTestFiles
{
    private static readonly Guid MeshId = Guid.Parse("c7b9ce30-1ce2-4a77-9f59-070aba70eb8f");

    internal static void WriteMesh(Mesh mesh, string path)
    {
        using var file = new MeshAsset(PageArena.Shared, MeshId) { Name = mesh.Name! };
        using var geometry = new MeshBlock { Bounds = mesh.Bounds, Regions = mesh.Regions };
        using var clusters = new ClusterMeshBlock { VertexEncoding = mesh.VertexEncoding };
        using PageBuffer payload = ReadPayload(mesh);
        clusters.SetPayload(payload, mesh.BvhOffset);
        file.SetBlock(geometry);
        file.SetBlock(clusters);
        (file).SaveAsync(path).AsTask().GetAwaiter().GetResult();
    }

    internal static PageBuffer ReadPayload(Mesh mesh)
    {
        using Mesh copy = mesh.Duplicate();
        var result = new PageBuffer(copy.Pages, checked((long)(copy.BvhOffset + copy.BvhLength)));
        try
        {
            for (int index = 0; index < copy.PageCount; index++)
            {
                if (!copy.TryRetainPage(index, out var page)) throw new InvalidOperationException("The fixture mesh has missing pages.");
                using (page) Copy(page, checked((long)copy.PageDigests[index].Offset));
            }
            using var bvh = copy.RetainBvh();
            Copy(bvh, checked((long)copy.BvhOffset));
            return result;
        }
        catch { result.Dispose(); throw; }

        void Copy(PageRange<byte> bytes, long offset)
        {
            foreach (ReadOnlyMemory<byte> segment in bytes.Sequence)
            {
                result.Write(offset, segment.Span);
                offset += segment.Length;
            }
        }
    }

    internal static async ValueTask<MeshFileContent> OpenMeshAsync(string path)
    {
        using var file = await MeshAsset.OpenAsync(path);
        return await ReadMeshAsync(file);
    }

    internal static async ValueTask<MeshFileContent> OpenMeshAsync(IRangeSource source, bool ownsSource,
        CancellationToken cancellationToken = default)
    {
        await using var loader = new AssetLoader(new RangeStorage(source, ownsSource));
        using var file = await MeshAsset.OpenAsync(loader, new AssetId<MeshAsset>(MeshId), cancellationToken);
        return await ReadMeshAsync(file, cancellationToken);
    }

    private static async ValueTask<MeshFileContent> ReadMeshAsync(MeshAsset file, CancellationToken cancellationToken = default)
    {
        Mesh mesh = await file.GetBlock<ClusterMeshBlock>().CreateAsync(file.GetBlock<MeshBlock>(), file.Name, cancellationToken);
        try { return new(mesh, await file.GetBlock<ClusterMeshBlock>().OpenPayloadSourceAsync(cancellationToken)); }
        catch { mesh.Dispose(); throw; }
    }

    internal static async ValueTask<Shader> OpenShaderAsync(string path)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(path);
        return await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
    }

    private sealed class RangeStorage(IRangeSource source, bool ownsSource) : IAssetStorage
    {
        private readonly AssetEntry _entry = new(MeshId, MeshAsset.FileType, MeshAsset.SchemaHash, Guid.NewGuid());
        public bool TryFind(Guid id, out AssetEntry entry) { entry = _entry; return id == MeshId; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ownsSource ? source : new BorrowedBytes(source));
    }

    private sealed class BorrowedBytes(IRangeSource source) : IRangeSource
    {
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
            => source.ReadExactlyAsync(offset, destination, cancellationToken);
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => source.AcquireAsync(offset, length, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class MeshFileContent(Mesh mesh, MeshPayloadSource source) : IDisposable
{
    internal Mesh Mesh { get; } = mesh;
    internal MeshPayloadSource PageSource { get; } = source;
    public void Dispose()
    {
        try { Mesh.Dispose(); }
        finally { PageSource.Dispose(); }
    }
}
