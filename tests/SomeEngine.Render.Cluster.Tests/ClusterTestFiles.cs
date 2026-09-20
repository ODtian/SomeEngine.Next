using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ClusterTestFiles
{
    private static readonly Guid MeshId = Guid.Parse("c7b9ce30-1ce2-4a77-9f59-070aba70eb8f");

    internal static void WriteMesh(Mesh mesh, string path)
    {
        using Mesh.Asset file = new Mesh.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Mesh>(MeshId)) { Name = mesh.Name, Bounds = mesh.Bounds,
            VertexEncoding = mesh.VertexEncoding, Payload = mesh.Payload, BvhOffset = mesh.BvhOffset,
            Regions = mesh.Regions, MaterialGuids = mesh.MaterialGuids,
        };
        (file).SaveAsync(path).AsTask().GetAwaiter().GetResult();
    }

    internal static async ValueTask<Mesh> OpenMeshAsync(string path)
    {
        await using Mesh.Asset file = await Mesh.Asset.OpenAsync(path);
        return await Mesh.CreateAsync(file);
    }

    internal static async ValueTask<Mesh> OpenMeshAsync(IRangeSource source, bool ownsSource)
    {
        await using var loader = new AssetLoader(new RangeStorage(source, ownsSource));
        await using Mesh.Asset file = await Mesh.Asset.OpenAsync(loader, new AssetId<Mesh>(MeshId));
        return await Mesh.CreateAsync(file);
    }

    internal static async ValueTask<Shader> OpenShaderAsync(string path)
    {
        await using Shader.Asset file = await Shader.Asset.OpenAsync(path);
        return await Shader.CreateAsync(file);
    }

    private sealed class RangeStorage(IRangeSource source, bool ownsSource) : IAssetStorage
    {
        private readonly AssetEntry _entry = new(MeshId, Mesh.Asset.FileType, Mesh.Asset.SchemaHash, Guid.NewGuid());
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
