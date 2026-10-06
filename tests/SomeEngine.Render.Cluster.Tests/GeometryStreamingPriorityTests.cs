using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingPriorityTests
{
    [Fact]
    public async Task APageReadBudgetLoadsTheGreaterProjectedDetailDemandFirst()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-GeometryPriority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var (positions, triangles, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(4);
            using MeshAsset built = ClusterBuilder.ProcessRaw(positions, attributes, triangles,
                [new MeshMaterialSlot(Guid.Empty)], "Streaming sphere");
            using MeshAsset lowAsset = built.CreateCopy(Guid.NewGuid());
            using MeshAsset highAsset = built.CreateCopy(Guid.NewGuid());
            AssetEntry low = await lowAsset.SaveAsync(Path.Combine(directory, "low.mesh.asset"));
            AssetEntry high = await highAsset.SaveAsync(Path.Combine(directory, "high.mesh.asset"));
            var reads = new PageReadOrder();
            await using var loader = new AssetLoader(new ObservedMeshStorage([low, high], reads));
            using MeshAsset lowFile = await MeshAsset.OpenAsync(loader, new(low.AssetGuid));
            using MeshAsset highFile = await MeshAsset.OpenAsync(loader, new(high.AssetGuid));
            ClusterMeshBlock lowGeometry = lowFile.GetBlock<ClusterMeshBlock>();
            ClusterMeshBlock highGeometry = highFile.GetBlock<ClusterMeshBlock>();
            var lowMesh = await lowGeometry.CreateAsync(lowFile.GetBlock<MeshBlock>(), lowFile.Name);
            var highMesh = await highGeometry.CreateAsync(highFile.GetBlock<MeshBlock>(), highFile.Name);
            using var lowSource = await lowGeometry.OpenPayloadSourceAsync();
            using var highSource = await highGeometry.OpenPayloadSourceAsync();
            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true,
                    EnableFrameMetricsReadback = true },
                await RasterTestGeometry.CreatePlaneAsync(), residencyOptions: new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = 8 * 1024 * 1024,
                    BvhCapacityBytes = 1024 * 1024,
                    PageFaultCapacity = 1024,
                    MaxConcurrentPageLoads = 1,
                });
            scene.SetGeometryVisible(false);
            scene.AddStreamingMesh(lowMesh, lowSource, new(new Vector3(-1, 0, -8), Quaternion.Identity, .5f));
            scene.AddStreamingMesh(highMesh, highSource, new(new Vector3(1, 0, -4), Quaternion.Identity, 1f));
            await scene.ReadNextRasterImageAsync();
            reads.Observe = true;
            try
            {
                scene.PumpStreaming();
                Guid first = await reads.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(high.AssetGuid, first);
                Assert.Single(reads.Started);
            }
            finally { reads.Continue.TrySetResult(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PageReadOrder
    {
        internal bool Observe;
        internal TaskCompletionSource<Guid> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<Guid> Started { get; } = [];

        internal async ValueTask BeforeReadAsync(Guid mesh, CancellationToken cancellationToken)
        {
            if (!Observe) return;
            lock (Started) Started.Add(mesh);
            First.TrySetResult(mesh);
            await Continue.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ObservedMeshStorage(AssetEntry[] entries, PageReadOrder reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry entry)
        {
            entry = entries.FirstOrDefault(value => value.AssetGuid == id);
            return entry.AssetGuid == id;
        }

        public ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IRangeSource>(new ObservedMeshReads(FileRangeSource.Open(entry.Path), entry.AssetGuid, reads));
    }

    private sealed class ObservedMeshReads(IRangeSource bytes, Guid mesh, PageReadOrder reads) : IRangeSource
    {
        public long Length => bytes.Length;
        public string Generation => bytes.Generation;
        public bool LeasesAreImmutable => bytes.LeasesAreImmutable;
        public bool RetainsResidentBacking => bytes.RetainsResidentBacking;

        public async ValueTask<RangeLease> AcquireAsync(long offset, int length,
            CancellationToken cancellationToken = default)
        {
            await reads.BeforeReadAsync(mesh, cancellationToken);
            return await bytes.AcquireAsync(offset, length, cancellationToken);
        }

        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            await reads.BeforeReadAsync(mesh, cancellationToken);
            await bytes.ReadExactlyAsync(offset, destination, cancellationToken);
        }

        public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
}
