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

    [Fact]
    public async Task AVisibleResidentPartReloadsItsEvictedOffscreenDetailDependency()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-GeometryDependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new DetailReads();
        try
        {
            using MeshAsset asset = await CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (9f, 11f, 0f), (-1f, 1f, 0f)], new(5, 0, -.25f, 6.5f));
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "separated-detail.mesh.asset"));
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
            reads.SecondContinue.TrySetResult();
            await reads.SecondFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));

            // A second mesh needs one detail page. The bounded heap releases the first
            // mesh's older, offscreen part while its visible detail part stays resident.
            using MeshAsset competitorFile = await CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
            ClusterMeshBlock competitorGeometry = competitorFile.GetBlock<ClusterMeshBlock>();
            var competitorMesh = await competitorGeometry.CreateAsync(competitorFile.GetBlock<MeshBlock>(), competitorFile.Name);
            using var competitorSource = await competitorGeometry.OpenPayloadSourceAsync();
            scene.RemoveMaterialMesh(first);
            var competitor = scene.AddStreamingMesh(competitorMesh, competitorSource, transform);
            AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));
            scene.RemoveMaterialMesh(competitor);
            scene.AddStreamingMesh(mesh, source, transform);
            AssertImage(coarse, await scene.ReadNextRasterImageAsync());

            reads.ReloadContinue.TrySetResult();
            AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));
        }
        finally
        {
            reads.FirstContinue.TrySetResult();
            reads.SecondContinue.TrySetResult();
            reads.ReloadContinue.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public async Task VisibleDetailStaysResidentWhenNewDetailDisplacesColdGeometry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-VisibleGeometry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new DetailReads();
        try
        {
            using MeshAsset asset = await CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "visible-detail.mesh.asset"));
            await using var loader = new AssetLoader(new GatedStorage(entry, reads));
            using MeshAsset file = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
            ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
            var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
            using var source = await geometry.OpenPayloadSourceAsync();
            reads.PageBytes = mesh.GetPageByteLength(1);
            var left = new SomeEngine.Core.Math.TransformQvvs(new Vector3(-1.25f, 0, -4), Quaternion.Identity);
            var right = new SomeEngine.Core.Math.TransformQvvs(new Vector3(1.25f, 0, -4), Quaternion.Identity);
            RasterImage visibleFine, bothFine;
            await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
            {
                reference.SetGeometryVisible(false);
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), left);
                visibleFine = await reference.ReadNextRasterImageAsync();
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), right);
                bothFine = await reference.ReadNextRasterImageAsync();
            }
            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
                residencyOptions: new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = 2560, BvhCapacityBytes = 65536,
                    PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                });
            scene.SetGeometryVisible(false);
            var visible = scene.AddStreamingMesh(mesh, source, left);
            RasterImage coarse = await scene.ReadNextRasterImageAsync();
            Assert.NotEqual(visibleFine.Depth, coarse.Depth);
            reads.Observe = true;
            scene.PumpStreaming();
            await reads.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
            reads.FirstContinue.TrySetResult();
            await reads.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            AssertImage(visibleFine, await ReadUntilDepthAsync(scene, visibleFine.Depth));

            // Load a second detail after the first, then leave it out of the view.
            // Its page is newer by installation time, but it is no longer demanded.
            using MeshAsset coldFile = asset.CreateCopy(Guid.NewGuid());
            ClusterMeshBlock coldGeometry = coldFile.GetBlock<ClusterMeshBlock>();
            var coldMesh = await coldGeometry.CreateAsync(coldFile.GetBlock<MeshBlock>(), coldFile.Name);
            using var coldSource = await coldGeometry.OpenPayloadSourceAsync();
            scene.RemoveMaterialMesh(visible);
            var cold = scene.AddStreamingMesh(coldMesh, coldSource, left);
            AssertImage(visibleFine, await ReadUntilDepthAsync(scene, visibleFine.Depth));
            scene.RemoveMaterialMesh(cold);
            scene.AddStreamingMesh(mesh, source, left);
            for (int frame = 0; frame < 4; frame++)
                AssertImage(visibleFine, await scene.ReadNextRasterImageAsync());

            // Three coarse pages and two fine pages fit. Loading the third fine
            // page must release the cold detail, not the continuously visible one.
            using MeshAsset newcomerFile = asset.CreateCopy(Guid.NewGuid());
            ClusterMeshBlock newcomerGeometry = newcomerFile.GetBlock<ClusterMeshBlock>();
            var newcomerMesh = await newcomerGeometry.CreateAsync(newcomerFile.GetBlock<MeshBlock>(), newcomerFile.Name);
            using var newcomerSource = await newcomerGeometry.OpenPayloadSourceAsync();
            scene.AddStreamingMesh(newcomerMesh, newcomerSource, right);
            AssertImage(bothFine, await ReadUntilDepthAsync(scene, bothFine.Depth));
        }
        finally
        {
            reads.FirstContinue.TrySetResult();
            reads.SecondContinue.TrySetResult();
            reads.ReloadContinue.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingGroupPagesReachFineDetailWithoutPriorityCycles(bool crossParentPages)
    {
        var transform = new SomeEngine.Core.Math.TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
        RasterImage fine;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
        {
            reference.SetGeometryVisible(false);
            reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
            fine = await reference.ReadNextRasterImageAsync();
        }
        using MeshAsset file = await CreateOverlappingStreamingPlaneAsync(crossParentPages);
        ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
        using var source = await geometry.OpenPayloadSourceAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
            residencyOptions: new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = 2048, BvhCapacityBytes = 65536,
                PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
            });
        scene.SetGeometryVisible(false);
        scene.AddStreamingMesh(mesh, source, transform);
        Assert.NotEqual(fine.Depth, (await scene.ReadNextRasterImageAsync()).Depth);
        AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));
    }

    [Fact]
    public async Task ColdCrossParentPagesReleaseCapacityForNewVisibleDetail()
    {
        var transform = new SomeEngine.Core.Math.TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
        RasterImage fine;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
        {
            reference.SetGeometryVisible(false);
            reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
            fine = await reference.ReadNextRasterImageAsync();
        }
        using MeshAsset file = await CreateOverlappingStreamingPlaneAsync(crossParentPages: true);
        ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
        using var source = await geometry.OpenPayloadSourceAsync();
        using MeshAsset nextFile = await CreateStreamingPartsAsync(
            [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
        ClusterMeshBlock nextGeometry = nextFile.GetBlock<ClusterMeshBlock>();
        var nextMesh = await nextGeometry.CreateAsync(nextFile.GetBlock<MeshBlock>(), nextFile.Name);
        using var nextSource = await nextGeometry.OpenPayloadSourceAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
            residencyOptions: new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = 2048, BvhCapacityBytes = 65536,
                PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
            });
        scene.SetGeometryVisible(false);
        var previous = scene.AddStreamingMesh(mesh, source, transform);
        RasterImage coarse = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(fine.Depth, coarse.Depth);
        AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));

        // Both old detail pages contain a parent of the other page's finer part.
        // Once that mesh leaves the view, the whole cold set can release capacity.
        scene.RemoveMaterialMesh(previous);
        var newcomer = scene.AddStreamingMesh(nextMesh, nextSource, transform);
        AssertImage(coarse, await scene.ReadNextRasterImageAsync());
        AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));

        scene.RemoveMaterialMesh(newcomer);
        scene.AddStreamingMesh(mesh, source, transform);
        AssertImage(coarse, await scene.ReadNextRasterImageAsync());
        AssertImage(fine, await ReadUntilDepthAsync(scene, fine.Depth));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(16, false)]
    public async Task OnlyCompleteDemandChangesLodUnderSustainedBudgetPressure(int feedbackCapacity, bool keepsFineDetail)
    {
        var transform = new SomeEngine.Core.Math.TransformQvvs(new Vector3(0, 0, -24), Quaternion.Identity);
        RasterImage fine;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, LodThreshold = 1.0f, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
        {
            reference.SetGeometryVisible(false);
            reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
            fine = await reference.ReadNextRasterImageAsync();
        }
        using MeshAsset file = await CreateStreamingPlaneAsync();
        ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
        using var source = await geometry.OpenPayloadSourceAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, LodThreshold = 1.0f, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
            residencyOptions: new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = 1536, BvhCapacityBytes = 65536,
                PageFaultCapacity = feedbackCapacity, MaxConcurrentPageLoads = 1,
            });
        scene.SetGeometryVisible(false);
        scene.AddStreamingMesh(mesh, source, transform);
        Assert.NotEqual(fine.Depth, (await scene.ReadNextRasterImageAsync()).Depth);
        RasterImage image = await ReadUntilDepthAsync(scene, fine.Depth);
        AssertImage(fine, image);
        // Both fine pages remain visible. One record is always an incomplete
        // observation, while a complete report exposes sustained pool pressure.
        for (int frame = 0; frame < 36; frame++)
            image = await scene.ReadNextRasterImageAsync();
        if (keepsFineDetail) AssertImage(fine, image);
        else Assert.NotEqual(fine.Depth, image.Depth);
    }

    [Fact]
    public async Task DeferredDetailLoadsAfterTheViewReleasesProtectedGeometry()
    {
        var left = new SomeEngine.Core.Math.TransformQvvs(new Vector3(-1.25f, 0, -4), Quaternion.Identity);
        var right = new SomeEngine.Core.Math.TransformQvvs(new Vector3(1.25f, 0, -4), Quaternion.Identity);
        RasterImage leftFine, rightFine, leftFineRightCoarse;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync()))
        {
            reference.SetGeometryVisible(false);
            var first = reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), left);
            leftFine = await reference.ReadNextRasterImageAsync();
            var second = reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(),
                new SomeEngine.Core.Math.TransformQvvs(new Vector3(1.25f, 0, -4.5f), Quaternion.Identity));
            leftFineRightCoarse = await reference.ReadNextRasterImageAsync();
            reference.RemoveMaterialMesh(first);
            reference.RemoveMaterialMesh(second);
            reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), right);
            rightFine = await reference.ReadNextRasterImageAsync();
        }
        using MeshAsset file = await CreateStreamingPartsAsync(
            [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
        ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
        using var source = await geometry.OpenPayloadSourceAsync();
        using MeshAsset nextFile = file.CreateCopy(Guid.NewGuid());
        ClusterMeshBlock nextGeometry = nextFile.GetBlock<ClusterMeshBlock>();
        var nextMesh = await nextGeometry.CreateAsync(nextFile.GetBlock<MeshBlock>(), nextFile.Name);
        using var nextSource = await nextGeometry.OpenPayloadSourceAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true }, await RasterTestGeometry.CreatePlaneAsync(),
            residencyOptions: new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = 1536, BvhCapacityBytes = 65536,
                PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
            });
        scene.SetGeometryVisible(false);
        var visible = scene.AddStreamingMesh(mesh, source, left);
        AssertImage(leftFine, await ReadUntilDepthAsync(scene, leftFine.Depth));
        scene.AddStreamingMesh(nextMesh, nextSource, right);
        for (int frame = 0; frame < 4; frame++)
            AssertImage(leftFineRightCoarse, await scene.ReadNextRasterImageAsync());
        scene.RemoveMaterialMesh(visible);
        AssertImage(rightFine, await ReadUntilDepthAsync(scene, rightFine.Depth));
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

    private static Task<MeshAsset> CreateStreamingPlaneAsync() => CreateStreamingPartsAsync(
        [(-1f, 1f, -.5f), (-1f, 0f, 0f), (0f, 1f, 0f)], new(0, 0, -.25f, 1.5f));

    private static async Task<MeshAsset> CreateStreamingPartsAsync(
        (float Left, float Right, float Depth)[] surfaces, Vector4 lodBounds)
    {
        var meshes = new List<SomeEngine.Resources.Mesh>();
        foreach ((float left, float right, float depth) in surfaces)
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
        for (int child = 0; child < meshes.Count; child++)
        {
            var slice = MemoryMarshal.Cast<byte, ClusterBVHNode>(meshes[child].Bvh.Span)[0].GetChild(0);
            slice.LODBounds = lodBounds;
            slice.ChildReference = (uint)child << 8;
            slice.PageRangeKey = child == 0 ? 1u : (1u << 5) | (uint)(meshes.Count - 1) | 0x40000000u;
            slice.SetErrors(child == 0 ? .5f : 0f, child == 0 ? float.PositiveInfinity : .5f);
            nodes[0].SetChild(child, slice);
        }
        byte[] bvh = MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray();
        byte[] payload = [.. pages.SelectMany(static page => page), .. bvh];
        var result = new MeshAsset(Guid.NewGuid()) { Name = "Streamed plane detail group" };
        result.SetBlock(new MeshBlock { Bounds = new() { Radius = new Vector3(lodBounds.X, lodBounds.Y, lodBounds.Z).Length() + lodBounds.W } });
        using var geometry = new ClusterMeshBlock();
        geometry.VertexEncoding = meshes[0].VertexEncoding;
        geometry.ClusterGroups =
        [
            new() { FirstPage = 0, PageCount = 1, HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 0, PageIndex = 0 }] },
            new() { FirstPage = 1, PageCount = (uint)(meshes.Count - 1), Parents = [new() { PageIndex = 0, ClusterIndex = 0 }],
                HierarchyParts = [.. Enumerable.Range(1, meshes.Count - 1).Select(child =>
                    new MeshHierarchyPart { NodeIndex = 0, ChildIndex = (uint)child, PageIndex = (uint)child })] },
        ];
        geometry.SetPayload(payload, (ulong)(payload.Length - bvh.Length));
        result.SetBlock(geometry);
        return result;
    }

    private static async Task<MeshAsset> CreateOverlappingStreamingPlaneAsync(bool crossParentPages)
    {
        using MeshAsset parts = await CreateStreamingPartsAsync(
            [(-1f, 1f, -.5f), (-1f, 1f, -.25f), (-1f, 0f, 0f), (0f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
        ClusterMeshBlock partsGeometry = parts.GetBlock<ClusterMeshBlock>();
        var mesh = await partsGeometry.CreateAsync(parts.GetBlock<MeshBlock>(), parts.Name);
        await partsGeometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        var pages = Enumerable.Range(0, mesh.PageCount).Select(index =>
        {
            mesh.TryGetPage(index, out var bytes);
            return bytes.ToArray();
        }).ToArray();
        // One physical page holds the middle-level parent and a finer child.
        // Its middle group and the two-page fine group have overlapping ranges.
        byte[] shared = CombinePlanePages(pages[1], pages[2]);
        ref PackedCluster middle = ref MemoryMarshal.Cast<byte, PackedCluster>(shared.AsSpan(MeshPageHeader.Size))[0];
        middle.Flags = PackedCluster.StreamingLeaf;
        middle.LODErrorHalf = BitConverter.HalfToUInt16Bits((Half).25f);
        var nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(mesh.Bvh.Span).ToArray();
        var middleSlice = nodes[0].GetChild(1);
        middleSlice.PageRangeKey = (1u << 5) | 1u | 0x40000000u;
        middleSlice.SetErrors(.25f, .5f);
        nodes[0].SetChild(1, middleSlice);
        for (int child = 2; child < 4; child++)
        {
            var slice = nodes[0].GetChild(child);
            slice.ChildReference = child == 2 ? (1u << 8) | 1u : 2u << 8;
            slice.PageRangeKey = (1u << 5) | 2u | 0x40000000u;
            slice.SetErrors(0f, .25f);
            nodes[0].SetChild(child, slice);
        }
        byte[] lastPage = pages[3];
        if (crossParentPages)
        {
            lastPage = CombinePlanePages(pages[1], pages[3]);
            ref PackedCluster otherMiddle = ref MemoryMarshal.Cast<byte, PackedCluster>(lastPage.AsSpan(MeshPageHeader.Size))[0];
            otherMiddle.Flags = PackedCluster.StreamingLeaf;
            otherMiddle.LODErrorHalf = BitConverter.HalfToUInt16Bits((Half).25f);
            ClusterBVHNode original = nodes[0];
            nodes = new ClusterBVHNode[3];
            nodes[0].SetChild(0, original.GetChild(0));
            var branch = original.GetChild(0);
            branch.BoundsCenter = new(0, 0, -.25f);
            branch.BoundsExtent = new(1, 1, .25f);
            branch.PageRangeKey = uint.MaxValue;
            branch.GroupPartData = (511u << 16) | 0xFFFFu;
            branch.SetErrors(0f, float.PositiveInfinity);
            branch.ChildReference = 1;
            nodes[0].SetChild(1, branch);
            branch.ChildReference = 2;
            nodes[0].SetChild(2, branch);
            nodes[1].SetChild(0, original.GetChild(1));
            var other = original.GetChild(1);
            other.ChildReference = 2u << 8;
            other.PageRangeKey = (2u << 5) | 1u | 0x40000000u;
            nodes[1].SetChild(1, other);
            var left = original.GetChild(2);
            left.PageRangeKey = (1u << 5) | 1u | 0x40000000u;
            nodes[2].SetChild(0, left);
            var right = original.GetChild(3);
            right.ChildReference = (2u << 8) | 1u;
            right.PageRangeKey = (2u << 5) | 1u | 0x40000000u;
            nodes[2].SetChild(1, right);
        }
        byte[] bvh = MemoryMarshal.AsBytes(nodes.AsSpan()).ToArray();
        byte[] payload = [.. pages[0], .. shared, .. lastPage, .. bvh];
        var result = new MeshAsset(Guid.NewGuid()) { Name = "Overlapping streamed groups" };
        result.SetBlock(new MeshBlock { Bounds = new() { Radius = 1.75f } });
        using var geometry = new ClusterMeshBlock();
        geometry.VertexEncoding = mesh.VertexEncoding;
        geometry.ClusterGroups =
        [
            new() { FirstPage = 0, PageCount = 1, HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 0, PageIndex = 0 }] },
            new() { FirstPage = 1, PageCount = 1, Parents = [new() { PageIndex = 0, ClusterIndex = 0 }],
                HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 1, PageIndex = 1 }] },
            new() { FirstPage = 1, PageCount = 2, Parents = [new() { PageIndex = 1, ClusterIndex = 0 }],
                HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 2, PageIndex = 1, FirstCluster = 1 },
                    new() { NodeIndex = 0, ChildIndex = 3, PageIndex = 2 }] },
        ];
        if (crossParentPages)
            geometry.ClusterGroups =
            [
                new() { FirstPage = 0, PageCount = 1, HierarchyParts = [new() { NodeIndex = 0, ChildIndex = 0, PageIndex = 0 }] },
                new() { FirstPage = 1, PageCount = 1, Parents = [new() { PageIndex = 0, ClusterIndex = 0 }],
                    HierarchyParts = [new() { NodeIndex = 1, ChildIndex = 0, PageIndex = 1 }] },
                new() { FirstPage = 2, PageCount = 1, Parents = [new() { PageIndex = 0, ClusterIndex = 0 }],
                    HierarchyParts = [new() { NodeIndex = 1, ChildIndex = 1, PageIndex = 2 }] },
                new() { FirstPage = 1, PageCount = 1, Parents = [new() { PageIndex = 2, ClusterIndex = 0 }],
                    HierarchyParts = [new() { NodeIndex = 2, ChildIndex = 0, PageIndex = 1, FirstCluster = 1 }] },
                new() { FirstPage = 2, PageCount = 1, Parents = [new() { PageIndex = 1, ClusterIndex = 0 }],
                    HierarchyParts = [new() { NodeIndex = 2, ChildIndex = 1, PageIndex = 2, FirstCluster = 1 }] },
            ];
        geometry.SetPayload(payload, (ulong)(payload.Length - bvh.Length));
        result.SetBlock(geometry);
        return result;
    }

    private static byte[] CombinePlanePages(byte[] first, byte[] second)
    {
        MeshPageHeader a = MemoryMarshal.Read<MeshPageHeader>(first);
        MeshPageHeader b = MemoryMarshal.Read<MeshPageHeader>(second);
        PackedCluster firstCluster = MemoryMarshal.Read<PackedCluster>(first.AsSpan((int)a.ClustersOffset));
        PackedCluster secondCluster = MemoryMarshal.Read<PackedCluster>(second.AsSpan((int)b.ClustersOffset));
        secondCluster.VertexBlobOffset = a.VertexBlobBytes;
        secondCluster.TriangleStart = (ushort)a.TotalTriangleCount;
        var header = new MeshPageHeader
        {
            ClusterCount = 2, TotalVertexCount = a.TotalVertexCount + b.TotalVertexCount,
            TotalTriangleCount = a.TotalTriangleCount + b.TotalTriangleCount,
            VertexBlobBytes = a.VertexBlobBytes + b.VertexBlobBytes,
            ClustersOffset = MeshPageHeader.Size, VertexBlobsOffset = MeshPageHeader.Size + 2 * PackedCluster.SizeInBytes,
            VertexEncodingHash = a.VertexEncodingHash,
        };
        header.IndicesOffset = header.VertexBlobsOffset + header.VertexBlobBytes;
        header.ExtensionDataOffset = header.IndicesOffset + header.TotalTriangleCount * 3;
        byte[] result = new byte[header.ExtensionDataOffset];
        MemoryMarshal.Write(result, in header);
        MemoryMarshal.Write(result.AsSpan((int)header.ClustersOffset), in firstCluster);
        MemoryMarshal.Write(result.AsSpan((int)header.ClustersOffset + PackedCluster.SizeInBytes), in secondCluster);
        first.AsSpan((int)a.VertexBlobsOffset, (int)a.VertexBlobBytes).CopyTo(result.AsSpan((int)header.VertexBlobsOffset));
        second.AsSpan((int)b.VertexBlobsOffset, (int)b.VertexBlobBytes).CopyTo(result.AsSpan((int)(header.VertexBlobsOffset + a.VertexBlobBytes)));
        first.AsSpan((int)a.IndicesOffset, (int)a.TotalTriangleCount * 3).CopyTo(result.AsSpan((int)header.IndicesOffset));
        second.AsSpan((int)b.IndicesOffset, (int)b.TotalTriangleCount * 3).CopyTo(result.AsSpan((int)(header.IndicesOffset + a.TotalTriangleCount * 3)));
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
