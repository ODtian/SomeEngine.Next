using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPipelineOutputTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RepeatedFaultDemandPreservesCoarseCoverageUntilCompleteDetailAndIgnoresDiscardedFrames()
    {
        using MeshAsset asset = await GeometryStreamingHandoffTests.CreateStreamingPlaneAsync();
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        var mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        using var payload = await geometry.OpenPayloadSourceAsync();
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var transform = new TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
        var options = new ClusterPipelineOptions
        { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true };
        RasterImage fine;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", false, options, plane))
        {
            reference.SetGeometryVisible(false);
            reference.AddGeometryMesh(plane, transform);
            fine = await reference.ReadNextRasterImageAsync();
        }

        await using var scene = await FrameStageScene.CreateAsync("D3D12", false, options, plane,
            residencyOptions: new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = 2048, BvhCapacityBytes = 65536,
                PageFaultCapacity = 16,
            });
        scene.SetGeometryVisible(false);
        scene.AddStreamingMesh(mesh, payload, transform);
        var completed = new CompletedPipelineFrames();
        scene.ObserveCompletedFrames(completed);
        RasterImage coarse = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(fine.Depth, coarse.Depth);
        Assert.Single(completed.Frames);
        ClusterStreamingFeedbackStatistics received = scene.FeedbackStatistics;
        Assert.True(received.ReceivedFrames > 0);
        Assert.True(received.ReportedPageFaults > 0);

        ulong discarded = await scene.DiscardNextFrameAsync();
        scene.CollectCompletedFrames();
        Assert.Single(completed.Frames);
        Assert.Equal(received.ReceivedFrames, scene.FeedbackStatistics.ReceivedFrames);
        Assert.Equal(received.ReportedPageFaults, scene.FeedbackStatistics.ReportedPageFaults);
        Assert.Equal(received.LastEpoch, scene.FeedbackStatistics.LastEpoch);
        RasterImage.AssertEqual(coarse, scene.ReadCurrentRasterImage(), "fault-discarded-frame");
        scene.PumpStreaming();
        RasterImage actual = coarse;
        for (int frame = 0; frame < 25 && !fine.Depth.AsSpan().SequenceEqual(actual.Depth); frame++)
        {
            actual = await scene.ReadNextRasterImageAsync();
            bool complete = fine.Depth.AsSpan().SequenceEqual(actual.Depth);
            RasterImage.AssertEqual(complete ? fine : coarse, actual, $"fault-atomic-handoff-{frame}");
        }
        RasterImage.AssertEqual(fine, actual, "fault-complete-detail");
        for (int frame = 0; frame < 4; frame++)
            RasterImage.AssertEqual(fine, await scene.ReadNextRasterImageAsync(), "fault-repeated-demand");
        Assert.DoesNotContain(completed.Frames, frame => frame.Identity == discarded);
        Assert.Equal(completed.Frames.Count, completed.Frames.Select(frame => frame.Identity).Distinct().Count());
        Assert.True(completed.Frames.Zip(completed.Frames.Skip(1),
            (previous, next) => previous.Identity < next.Identity).All(value => value));
        Assert.All(completed.Frames, frame => Assert.Equal(new byte[4], frame.Bytes));
        scene.PumpStreaming();
        ClusterStreamingFeedbackStatistics consumed = scene.FeedbackStatistics;
        Assert.Equal(consumed.ReceivedFrames, consumed.ConsumedFrames);
        Assert.Equal(consumed.ReportedPageFaults, consumed.ConsumedPageFaults);
        Assert.True(consumed.ReceivedFrames > received.ReceivedFrames);
        // Exact fault-inbox tie priority/request fields are not a public observable contract.
        // This checks the actual completed-frame/streaming result, not an allocation or cost claim.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [Trait("Category", "Hardware")]
    public async Task CasterRowsPreserveRenderedShadowsAcrossAgingClassChangesAndDiscard(int count)
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions
        { MaxCandidates = 65536, ForceHardwareRaster = true, EnableFrameMetricsReadback = true };
        var shadows = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            WorldUnitsPerCentimeter = 1, MaxFilterRadiusTexels = 0,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
            DynamicResolutionEnabled = false,
        };
        var capacity = new RenderInstanceOptions { RowCapacity = 128, BatchCapacity = 8 };
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, options, plane, shadows,
            instanceOptions: capacity);
        await using var reference = await FrameStageScene.CreateAsync("D3D12", true, options, plane, shadows,
            instanceOptions: capacity);
        scene.SetCasterTestMeshCount(count);
        reference.SetCasterTestMeshCount(count);
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)), new(SourceAngleDegrees: 0f));
        reference.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)), new(SourceAngleDegrees: 0f));
        DepthCacheClass[] automatic = Enumerable.Repeat(DepthCacheClass.Auto, count).ToArray();
        DepthCacheClass[] dynamicClasses = Enumerable.Repeat(DepthCacheClass.Dynamic, count).ToArray();
        scene.SetCasterTestClasses(automatic);
        reference.SetCasterTestClasses(dynamicClasses);
        for (int frame = 0; frame < 4; frame++)
        {
            await scene.ReadNextRasterImageAsync();
            await reference.ReadNextRasterImageAsync();
        }

        // Change real entities instead of seeding private ages or cache bit arrays.
        scene.MoveCasterTestMeshes(.025f);
        reference.MoveCasterTestMeshes(.025f);
        var moved = await scene.ReadShadowQualityAsync();
        RasterImage expected = await reference.ReadRasterImageAsync();
        RasterImage.AssertEqual(expected, moved.Image, $"caster-{count}-moved");
        if (count != 0)
        {
            Assert.Contains(MemoryMarshal.Cast<byte, float>(moved.Image.Depth).ToArray(),
                depth => float.IsFinite(depth) && depth > 0 && depth < 1);
            Assert.True(DynamicItems(moved.Quality) > 0);
        }
        for (int frame = 0; frame < 103; frame++)
            RasterImage.AssertEqual(expected, await scene.ReadNextRasterImageAsync(), $"caster-{count}-aging");
        var aged = await scene.ReadShadowQualityAsync();
        RasterImage.AssertEqual(expected, aged.Image, $"caster-{count}-aged");
        if (count != 0) Assert.True(StaticItems(aged.Quality) > 0);

        scene.SetCasterTestClasses(Enumerable.Repeat(DepthCacheClass.Static, count).ToArray());
        await scene.DiscardNextFrameAsync();
        RasterImage.AssertEqual(aged.Image, scene.ReadCurrentRasterImage(), $"caster-{count}-discarded");
        scene.SetCasterTestClasses(dynamicClasses);
        RasterImage.AssertEqual(expected, await scene.ReadNextRasterImageAsync(), $"caster-{count}-dynamic");
        scene.SetCasterTestClasses(Enumerable.Range(0, count).Select(row => (row % 4) switch
        {
            0 => DepthCacheClass.Static, 1 => DepthCacheClass.Dynamic,
            2 => DepthCacheClass.Auto, _ => (DepthCacheClass)uint.MaxValue,
        }).ToArray());
        scene.InvalidateCasterTestRows(Enumerable.Range(0, count).Where(row => row % 7 == 0)
            .Select(row => (uint)row).ToArray(), all: true);
        await scene.DiscardNextFrameAsync();
        RasterImage.AssertEqual(expected, await scene.ReadNextRasterImageAsync(), $"caster-{count}-mixed-retry");

        // Replacing membership must preserve the visible geometry, not stale row identities.
        scene.SetCasterTestMeshCount(count);
        scene.MoveCasterTestMeshes(.025f);
        RasterImage.AssertEqual(expected, await scene.ReadNextRasterImageAsync(), $"caster-{count}-membership");
        scene.SetCasterTestClasses(dynamicClasses);
        scene.ObserveCasterTestStage(() => throw new InvalidOperationException("Abandon staged caster frame"));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scene.DiscardNextFrameAsync());
        }
        finally { scene.ObserveCasterTestStage(null); }
        RasterImage.AssertEqual(expected, scene.ReadCurrentRasterImage(), $"caster-{count}-failed-stage");
        RasterImage.AssertEqual(expected, await scene.ReadNextRasterImageAsync(), $"caster-{count}-recovered");
        // Public quality and pixels cover row boundaries and completed-frame aging.
        // Private bit packing, array reuse and an artificially seeded exact age are intentionally not asserted.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Hardware")]
    public async Task GeometryProjectionOverridePreservesPublicOutputWhenRestored(bool customGeometryProjection)
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 4096, ForceHardwareRaster = true }, plane);
        scene.UseMaterial("Unlit");
        Matrix4x4 view = Matrix4x4.CreateLookAt(new(.3f, -.2f, 0), new(0, 0, -6), Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(.9f, 1f, .1f, 100f);
        projection.M31 += .00125f;
        projection.M32 -= .0025f;
        var source = new RenderView(view, projection, 64, 64);
        scene.SetView(source);
        RasterImage baseline = await scene.ReadRasterImageAsync();
        Assert.Contains(MemoryMarshal.Cast<byte, float>(baseline.Depth).ToArray(),
            depth => float.IsFinite(depth) && depth > 0 && depth < 1);
        scene.SetView(source with { GeometryProjection = projection });
        RasterImage.AssertEqual(baseline, await scene.ReadNextRasterImageAsync(), "geometry-explicit-primary");
        Matrix4x4 geometryProjection = customGeometryProjection
            ? Matrix4x4.CreateOrthographicOffCenter(-3f, 4f, -2f, 5f, .2f, 200f)
            : projection;
        scene.SetView(source with { GeometryProjection = geometryProjection });
        RasterImage overridden = await scene.ReadNextRasterImageAsync();
        if (customGeometryProjection)
        {
            Assert.Contains(MemoryMarshal.Cast<byte, float>(overridden.Depth).ToArray(),
                depth => float.IsFinite(depth) && depth > 0 && depth < 1);
            Assert.NotEqual(baseline.Depth, overridden.Depth);
        }
        else RasterImage.AssertEqual(baseline, overridden, "geometry-default-override");
        scene.SetView(source);
        RasterImage.AssertEqual(baseline, await scene.ReadNextRasterImageAsync(), "geometry-restored");
        // This public render test does not claim exact private matrix bytes or multiplication cost.
    }

    private static uint StaticItems(VirtualShadowFrameQuality quality) =>
        (uint)quality.Views.Sum(view => view.StaticHardwareVisibleItems + view.StaticSoftwareVisibleItems);

    private static uint DynamicItems(VirtualShadowFrameQuality quality) =>
        (uint)quality.Views.Sum(view => view.DynamicHardwareVisibleItems + view.DynamicSoftwareVisibleItems);

    private sealed class CompletedPipelineFrames : IGraphReadbackReceiver
    {
        internal List<(ulong Identity, byte[] Bytes)> Frames { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Frames.Add((tag, bytes.ToArray()));
    }
    [Fact]
    public void Output_description_requires_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClusterPipelineOutputDescription(1, 1, 0));

        var description = new ClusterPipelineOutputDescription(64, 32, 4);
        Assert.Equal(64, description.Width);
        Assert.Equal(32, description.Height);
        Assert.Equal(4u, description.ViewCount);
    }

    [Fact]
    public void Geometry_outputs_are_explicit_and_have_no_presentation_target()
    {
        var outputs = new ClusterPipelineOutputs(
            default,
            default,
            default,
            default,
            null!,
            64,
            32,
            4);

        Assert.False(outputs.IsValid);
        Assert.Equal(64, outputs.Width);
        Assert.Equal(32, outputs.Height);
        Assert.Equal(4u, outputs.ViewCount);
        Assert.DoesNotContain(
            typeof(ClusterPipelineOutputs).GetProperties(),
            static property => property.Name.Contains(
                "Present",
                StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed partial class FrameStageScene
{
    private List<Entity>? _casterTestMeshes;
    private Action? _observeCasterTestStage;
    internal void ObserveCasterTestStage(Action? observer) => _observeCasterTestStage = observer;

    internal void SetCasterTestMeshCount(int count)
    {
        _casterTestMeshes ??= new List<Entity>(_meshes);
        foreach (Entity entity in _casterTestMeshes) _gameWorld.DestroyEntity(entity);
        _casterTestMeshes.Clear();
        for (int row = 0; row < count; row++)
            _casterTestMeshes.Add(AddMesh(_mesh, CasterTestTransform(row, 0)));
    }

    internal void MoveCasterTestMeshes(float offset)
    {
        for (int row = 0; row < _casterTestMeshes!.Count; row++)
            SetMeshTransform(_casterTestMeshes[row], CasterTestTransform(row, offset));
    }

    private static TransformQvvs CasterTestTransform(int row, float offset) => row == 0
        ? new(new Vector3(offset, 0, -8), Quaternion.Identity) { Stretch = new(3, 3, .1f) }
        : new(new Vector3(offset - .42f + (row - 1) % 8 * .12f, 4 + (row - 1) / 8 * .12f, -4),
            Quaternion.Identity) { Stretch = new(.22f, .22f, .1f) };

    internal void SetCasterTestClasses(DepthCacheClass[] classes)
    {
        Assert.Equal(_casterTestMeshes!.Count, classes.Length);
        for (int row = 0; row < classes.Length; row++)
        {
            Entity entity = _casterTestMeshes[row];
            MeshInstance mesh = _gameWorld.Read<MeshInstance>(entity);
            if (mesh.DepthCacheClass == classes[row]) continue;
            mesh.DepthCacheClass = classes[row];
            _gameWorld.Replace(entity, mesh);
        }
    }

    internal void InvalidateCasterTestRows(uint[] rows, bool all)
    {
        if (all) _shadows.InvalidateAllCasters();
        _shadows.InvalidateCasters(rows);
    }
}
