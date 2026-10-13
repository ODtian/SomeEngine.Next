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
public sealed class GeometryStreamingRetryTests
{
    [Fact]
    public async Task ResidentDetailRetriesAnOffscreenSiblingBeforeReplacingCoarseCoverage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-GeometryRetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new DetailReads();
        try
        {
            using MeshAsset asset = await CreateStreamingPlanesAsync();
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "planes.mesh.asset"));
            await using var loader = new AssetLoader(new RetryStorage(entry, reads));
            using MeshAsset file = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
            ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
            var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
            using var source = await geometry.OpenPayloadSourceAsync();
            reads.PageBytes = mesh.GetPageByteLength(1);
            Assert.Equal(reads.PageBytes, mesh.GetPageByteLength(2));
            var transform = new SomeEngine.Core.Math.TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
            RasterImage fine;
            await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true },
                await RasterTestGeometry.CreatePlaneAsync()))
            {
                reference.SetGeometryVisible(false);
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
                fine = await reference.ReadNextRasterImageAsync();
            }

            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true },
                await RasterTestGeometry.CreatePlaneAsync(),
                residencyOptions: new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = 65536, BvhCapacityBytes = 65536,
                    PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                });
            scene.SetGeometryVisible(false);
            scene.AddStreamingMesh(mesh, source, transform);
            RasterImage coarse = await scene.ReadNextRasterImageAsync();
            Assert.NotEqual(fine.Depth, coarse.Depth);

            reads.Observe = true;
            scene.PumpStreaming();
            await reads.ResidentReadFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            for (int frame = 0; frame < 24 && !reads.MissingReadStarted.Task.IsCompleted; frame++)
                await scene.ReadNextRasterImageAsync();
            await reads.MissingReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // Publish the visible detail page while its offscreen sibling is still unavailable.
            AssertImage(coarse, await scene.ReadNextRasterImageAsync());
            reads.FailMissingRead.TrySetResult();
            await reads.MissingReadFailed.Task.WaitAsync(TimeSpan.FromSeconds(20));

            RasterImage recovered = await scene.ReadNextRasterImageAsync();
            for (int frame = 0; frame < 24 && !fine.Depth.AsSpan().SequenceEqual(recovered.Depth); frame++)
                recovered = await scene.ReadNextRasterImageAsync();
            AssertImage(fine, recovered);
            Assert.True(reads.RetriedReadFinished.Task.IsCompletedSuccessfully);
        }
        finally
        {
            reads.FailMissingRead.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertImage(RasterImage expected, RasterImage actual)
    {
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.Color),
            new(64, 64, Format.R16G16B16A16Float, actual.Color));
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
            new(64, 64, Format.D32Float, actual.Depth));
    }

    private static async Task<MeshAsset> CreateStreamingPlanesAsync()
    {
        Vector3[][] vertices =
        [
            [new(-1, -1, -.5f), new(1, -1, -.5f), new(1, 1, -.5f), new(-1, 1, -.5f),
             new(30, -1, -.5f), new(32, -1, -.5f), new(32, 1, -.5f), new(30, 1, -.5f)],
            [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)],
            [new(30, -1, 0), new(32, -1, 0), new(32, 1, 0), new(30, 1, 0)],
        ];
        var meshes = new List<SomeEngine.Resources.Mesh>();
        foreach (Vector3[] positions in vertices)
        {
            float[] normals = Enumerable.Range(0, positions.Length).SelectMany(static _ => new float[] { 0, 0, 1 }).ToArray();
            float[] texcoords = Enumerable.Range(0, positions.Length / 4).SelectMany(static _ => new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }).ToArray();
            using MeshAsset part = ClusterBuilder.ProcessRaw(positions,
                [new RawAttribute("NORMAL", normals, 3, ValueType.Int8, 3, true),
                 new RawAttribute("TEXCOORD_0", texcoords, 2, ValueType.Float16, 2, false)],
                positions.Length == 8 ? [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7] : [0, 1, 2, 0, 2, 3],
                [new MeshMaterialSlot(Guid.Empty)], "Streamed plane part");
            ClusterMeshBlock block = part.GetBlock<ClusterMeshBlock>();
            meshes.Add(await block.CreateAsync(part.GetBlock<MeshBlock>(), part.Name));
        }

        var pages = meshes.Select(mesh => { mesh.TryGetPage(0, out var page); return page.ToArray(); }).ToArray();
        ref PackedCluster coarse = ref MemoryMarshal.Cast<byte, PackedCluster>(pages[0].AsSpan(MeshPageHeader.Size))[0];
        coarse.Flags = PackedCluster.StreamingLeaf;
        coarse.LODErrorHalf = BitConverter.HalfToUInt16Bits((Half).5f);
        var nodes = new ClusterBVHNode[1];
        for (int child = 0; child < 3; child++)
        {
            var slice = MemoryMarshal.Cast<byte, ClusterBVHNode>(meshes[child].Bvh.Span)[0].GetChild(0);
            slice.LODBounds = new(15.5f, 0, -.25f, 17f);
            slice.ChildReference = (uint)child << 8;
            slice.PageRangeKey = child == 0 ? 1u : (1u << 5) | 2u | 0x40000000u;
            slice.SetErrors(child == 0 ? .5f : 0f, child == 0 ? float.PositiveInfinity : .5f);
            nodes[0].SetChild(child, slice);
        }

        byte[] bvh = MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray();
        byte[] payload = [.. pages.SelectMany(static page => page), .. bvh];
        var result = new MeshAsset(Guid.NewGuid()) { Name = "Visible detail with an offscreen sibling" };
        result.SetBlock(new MeshBlock { Bounds = new() { Center = new() { X = 15.5f, Z = -.25f }, Radius = 17 } });
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
        internal volatile bool Observe;
        internal int PageBytes;
        private int _reads;
        internal TaskCompletionSource ResidentReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MissingReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FailMissingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MissingReadFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RetriedReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask<int> BeforeReadAsync(int length, CancellationToken token)
        {
            if (!Observe || length != PageBytes) return 0;
            int read = Interlocked.Increment(ref _reads);
            if (read == 2)
            {
                MissingReadStarted.TrySetResult();
                await FailMissingRead.Task.WaitAsync(token);
                MissingReadFailed.TrySetResult();
                throw new IOException();
            }
            return read;
        }

        internal void Finished(int read)
        {
            if (read == 1) ResidentReadFinished.TrySetResult();
            if (read > 2) RetriedReadFinished.TrySetResult();
        }
    }

    private sealed class RetryStorage(AssetEntry entry, DetailReads reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken token = default) =>
            ValueTask.FromResult<IRangeSource>(new RetrySource(FileRangeSource.Open(selected.Path), reads));
    }

    private sealed class RetrySource(IRangeSource bytes, DetailReads reads) : IRangeSource
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
