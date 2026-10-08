using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization.IO;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingHandoffTests
{
    [Fact]
    public async Task PartialDetailKeepsCoarseCoverageAndEvictionRestoresItAfterTheCompleteHandoff()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-GeometryHandoff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new DetailReads();
        try
        {
            using MeshAsset asset = await CreateStreamingPlaneAsync();
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "plane.mesh.asset"));
            await using var loader = new AssetLoader(new GatedStorage(entry, reads));
            using MeshAsset file = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
            ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
            var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
            using var source = await geometry.OpenPayloadSourceAsync();
            reads.PageBytes = mesh.GetPageByteLength(1);
            var transform = new SomeEngine.Core.Math.TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
            RasterImage fine;
            await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
            {
                reference.SetGeometryVisible(false);
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
                fine = await reference.ReadNextRasterImageAsync();
            }
            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
                residencyOptions: new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = 2048, BvhCapacityBytes = 65536,
                    PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                });
            scene.SetGeometryVisible(false);
            var first = scene.AddStreamingMesh(mesh, source, transform);
            RasterImage coarse = await scene.ReadNextRasterImageAsync();
            Assert.NotEqual(fine.Depth, coarse.Depth);
            reads.Observe = true;
            scene.PumpStreaming();
            await reads.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
            reads.FirstContinue.TrySetResult();
            await reads.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            for (int frame = 0; frame < 24 && !reads.Second.Task.IsCompleted; frame++)
                await scene.ReadNextRasterImageAsync();
            await reads.Second.Task.WaitAsync(TimeSpan.FromSeconds(20));
            RasterImage partial = await scene.ReadNextRasterImageAsync();
            AssertImage(coarse, partial);
            reads.SecondContinue.TrySetResult();
            await reads.SecondFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            RasterImage complete = await ReadUntilDepthAsync(scene, fine.Depth);
            AssertImage(fine, complete);

            scene.RemoveMaterialMesh(first);
            using MeshAsset otherFile = asset.CreateCopy(Guid.NewGuid());
            ClusterMeshBlock otherGeometry = otherFile.GetBlock<ClusterMeshBlock>();
            var other = await otherGeometry.CreateAsync(otherFile.GetBlock<MeshBlock>(), otherFile.Name);
            using var otherSource = await otherGeometry.OpenPayloadSourceAsync();
            var competitor = scene.AddStreamingMesh(other, otherSource, transform);
            AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));
            scene.RemoveMaterialMesh(competitor);
            scene.AddStreamingMesh(mesh, source, transform);
            RasterImage restored = await scene.ReadNextRasterImageAsync();
            AssertImage(coarse, restored);
        }
        finally
        {
            reads.FirstContinue.TrySetResult();
            reads.SecondContinue.TrySetResult();
            reads.ReloadContinue.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<RasterImage> ReadUntilDepthAsync(FrameStageScene scene, byte[] depth)
    {
        RasterImage image = await scene.ReadNextRasterImageAsync();
        for (int frame = 0; frame < 24 && !depth.AsSpan().SequenceEqual(image.Depth); frame++)
            image = await scene.ReadNextRasterImageAsync();
        return image;
    }

    private static void AssertImage(RasterImage expected, RasterImage actual)
    {
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.Color),
            new(64, 64, Format.R16G16B16A16Float, actual.Color));
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
            new(64, 64, Format.D32Float, actual.Depth));
    }

    private static async Task<MeshAsset> CreateStreamingPlaneAsync()
    {
        var meshes = new List<SomeEngine.Resources.Mesh>();
        foreach ((float left, float right, float depth) in new[] { (-1f, 1f, -.5f), (-1f, 0f, 0f), (0f, 1f, 0f) })
        {
            Vector3[] positions = [new(left, -1, depth), new(right, -1, depth), new(right, 1, depth), new(left, 1, depth)];
            using MeshAsset plane = ClusterBuilder.ProcessRaw(positions,
                [new RawAttribute("NORMAL", [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], 3, ValueType.Int8, 3, true),
                 new RawAttribute("TEXCOORD_0", [(left + 1) / 2, 0, (right + 1) / 2, 0, (right + 1) / 2, 1, (left + 1) / 2, 1], 2, ValueType.Float16, 2, false)],
                [0, 1, 2, 0, 2, 3], [new MeshMaterialSlot(Guid.Empty)], "Streamed plane part");
            ClusterMeshBlock block = plane.GetBlock<ClusterMeshBlock>();
            var mesh = await block.CreateAsync(plane.GetBlock<MeshBlock>(), plane.Name);
            meshes.Add(mesh);
        }
        var pages = meshes.Select(mesh => { mesh.TryGetPage(0, out var page); return page.ToArray(); }).ToArray();
        ref PackedCluster coarse = ref MemoryMarshal.Cast<byte, PackedCluster>(pages[0].AsSpan(MeshPageHeader.Size))[0];
        coarse.Flags = PackedCluster.StreamingLeaf;
        coarse.LODErrorHalf = BitConverter.HalfToUInt16Bits((Half).5f);
        var nodes = new ClusterBVHNode[1];
        for (int child = 0; child < 3; child++)
        {
            var slice = MemoryMarshal.Cast<byte, ClusterBVHNode>(meshes[child].Bvh.Span)[0].GetChild(0);
            slice.LODBounds = new(0, 0, -.25f, 1.5f);
            slice.ChildReference = (uint)child << 8;
            slice.PageRangeKey = child == 0 ? 1u : (1u << 5) | 2u | 0x40000000u;
            slice.SetErrors(child == 0 ? .5f : 0f, child == 0 ? float.PositiveInfinity : .5f);
            nodes[0].SetChild(child, slice);
        }
        byte[] bvh = MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray();
        byte[] payload = [.. pages.SelectMany(static page => page), .. bvh];
        var result = new MeshAsset(Guid.NewGuid()) { Name = "Two-page detail handoff" };
        result.SetBlock(new MeshBlock { Bounds = new() { Radius = 2 } });
        using var geometry = new ClusterMeshBlock();
        geometry.VertexEncoding = meshes[0].VertexEncoding;
        geometry.ClusterGroups =
        [
            new() { FirstPage = 0, PageCount = 1, HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 0, PageIndex = 0 }] },
            new() { FirstPage = 1, PageCount = 2, Parents = [new() { PageIndex = 0, ClusterIndex = 0 }],
                HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 1, PageIndex = 1 }, new() { NodeIndex = 0, ChildIndex = 2, PageIndex = 2 }] },
        ];
        geometry.SetPayload(payload, (ulong)(payload.Length - bvh.Length));
        result.SetBlock(geometry);
        return result;
    }

    private sealed class DetailReads
    {
        internal bool Observe;
        internal int PageBytes;
        private int _reads;
        internal TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReloadContinue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask<int> BeforeReadAsync(int length, CancellationToken token)
        {
            if (!Observe || length != PageBytes) return 0;
            int read = Interlocked.Increment(ref _reads);
            if (read == 1) { First.TrySetResult(); await FirstContinue.Task.WaitAsync(token); }
            else if (read == 2) { Second.TrySetResult(); await SecondContinue.Task.WaitAsync(token); }
            else await ReloadContinue.Task.WaitAsync(token);
            return read;
        }

        internal void Finished(int read)
        {
            if (read == 1) FirstFinished.TrySetResult();
            if (read == 2) SecondFinished.TrySetResult();
        }
    }

    private sealed class GatedStorage(AssetEntry entry, DetailReads reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken token = default) =>
            ValueTask.FromResult<IRangeSource>(new GatedSource(FileRangeSource.Open(selected.Path), reads));
    }

    private sealed class GatedSource(IRangeSource bytes, DetailReads reads) : IRangeSource
    {
        public long Length => bytes.Length;
        public string Generation => bytes.Generation;
        public bool LeasesAreImmutable => bytes.LeasesAreImmutable;
        public bool RetainsResidentBacking => bytes.RetainsResidentBacking;
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken token = default)
        {
            int read = await reads.BeforeReadAsync(length, token);
            RangeLease result = await bytes.AcquireAsync(offset, length, token);
            reads.Finished(read);
            return result;
        }
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token = default)
        {
            int read = await reads.BeforeReadAsync(destination.Length, token);
            await bytes.ReadExactlyAsync(offset, destination, token);
            reads.Finished(read);
        }
        public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
}
