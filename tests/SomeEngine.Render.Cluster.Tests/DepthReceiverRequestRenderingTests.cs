using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DepthReceiverRequestRenderingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DepthRequestsPreserveForwardImageWithoutMaterialReceiverWork()
    {
        RasterImage expected;
        await using (var reference = await FrameStageScene.CreateAsync("D3D12", true, Options()))
        {
            reference.UseMaterial("Standard");
            reference.SetDepthReceiverRequests(false);
            expected = await reference.ReadRasterImageAsync();
            Assert.False(reference.UsesDepthReceiverRequests);
            AssertMaterialReceivers(reference.DepthReceiverWork);
            output.WriteLine($"Reference: early passes={reference.DepthReceiverWork.ReceiverPasses.Length}, receiver accesses={reference.DepthReceiverWork.ReceiverAccessCount}.");
        }

        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, Options());
        scene.UseMaterial("Standard");
        scene.SetDepthReceiverRequests(true);
        RasterImage actual = await scene.ReadRasterImageAsync();
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        Assert.True(scene.UsesDepthReceiverRequests);
        Assert.Null(scene.Outputs.SurfaceReceiverConsumers);
        Assert.Contains(MemoryMarshal.Cast<byte, float>(actual.Depth).ToArray(), value => value < 1f);
        Assert.Contains(MemoryMarshal.Cast<byte, Half>(actual.Color).ToArray(), value => (float)value > .01f);
        RasterImage.AssertEqual(expected, actual, "depth-requests-forward");
        AssertNoMaterialReceivers(scene.DepthReceiverWork);
        output.WriteLine($"Depth requests: early passes={scene.DepthReceiverWork.ReceiverPasses.Length}, receiver accesses={scene.DepthReceiverWork.ReceiverAccessCount}, first uses={scene.DepthReceiverWork.PrimaryFirstUse}/{scene.DepthReceiverWork.AdditionalFirstUse}.");
        ClusterPipelineOutputs outputs = scene.Outputs;

        // Dynamic first-person membership restores early receivers while retaining Forward.
        // The builtin adapter must retain the original deformed receiver coordinate contract.
        scene.SetView(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64,
            PlayerId: 1, FirstPerson: new FirstPersonCamera(1.2f, .8f)));
        scene.SetMeshFirstPerson(1, new(FirstPersonMeshKind.FirstPerson, 1));
        RasterImage firstPerson = await scene.ReadNextRasterImageAsync();
        Assert.False(scene.UsesDepthReceiverRequests);
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        AssertMaterialReceivers(scene.DepthReceiverWork);
        scene.SetDepthReceiverRequests(false);
        RasterImage explicitFirstPerson = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(firstPerson, explicitFirstPerson, "depth-requests-first-person-fallback");
        scene.SetDepthReceiverRequests(true);
        scene.SetMeshFirstPerson(1, default);
        scene.SetView(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64));
        actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.UsesDepthReceiverRequests);
        AssertNoMaterialReceivers(scene.DepthReceiverWork);
        RasterImage.AssertEqual(expected, actual, "depth-requests-after-first-person");

        // An actual saved entry with no PrimaryDepthReceiver declaration takes the same
        // conservative path as unknown custom evaluators, without changing shading mode.
        scene.UseMaterial("Unlit");
        RasterImage unmarked = await scene.ReadNextRasterImageAsync();
        Assert.False(scene.UsesDepthReceiverRequests);
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        AssertMaterialReceivers(scene.DepthReceiverWork);
        scene.SetDepthReceiverRequests(false);
        RasterImage.AssertEqual(unmarked, await scene.ReadNextRasterImageAsync(),
            "depth-requests-unmarked-fallback");
        scene.SetDepthReceiverRequests(true);
        scene.UseMaterial("Standard");
        actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.UsesDepthReceiverRequests);
        AssertNoMaterialReceivers(scene.DepthReceiverWork);
        RasterImage.AssertEqual(expected, actual, "depth-requests-after-unmarked");

        // Change the consumer demand without replacing the retained geometry or outputs.
        scene.SetDepthReceiverRequests(false);
        RasterImage restored = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, restored, "depth-requests-material-fallback");
        Assert.False(scene.UsesDepthReceiverRequests);
        AssertMaterialReceivers(scene.DepthReceiverWork);
        AssertOutputs(outputs, scene.Outputs);

        scene.SetDepthReceiverRequests(true);
        actual = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, actual, "depth-requests-restored");
        Assert.True(scene.UsesDepthReceiverRequests);
        AssertNoMaterialReceivers(scene.DepthReceiverWork);
        AssertOutputs(outputs, scene.Outputs);

        // Deferred still requests material receivers even when depth requests are enabled.
        scene.SetShadingPath(ClusterShadingPath.Deferred);
        await scene.ReadNextRasterImageAsync();
        Assert.Equal(ClusterShadingPath.Deferred, scene.InstalledShadingPath);
        Assert.False(scene.UsesDepthReceiverRequests);
        AssertMaterialReceivers(scene.DepthReceiverWork);
        AssertOutputs(outputs, scene.Outputs);
    }

    private static ClusterPipelineOptions Options() => new()
    {
        MaxCandidates = 4096,
        ShadingPath = ClusterShadingPath.Forward,
        ExportSurfaceReceivers = false,
        EnableFrameMetricsReadback = true,
    };

    private static void AssertMaterialReceivers(DepthReceiverFrame work)
    {
        Assert.NotEmpty(work.ReceiverPasses);
        Assert.True(work.ReceiverAccessCount > 0);
        Assert.True(work.PrimaryFirstUse >= 0);
    }

    private static void AssertNoMaterialReceivers(DepthReceiverFrame work)
    {
        Assert.Empty(work.ReceiverPasses);
        // Public RG accesses cover both the receiver clears in shade counting and
        // the material/mark consumers. No scheduled access means no active payload.
        Assert.Equal(0, work.ReceiverAccessCount);
        Assert.Equal(-1, work.PrimaryFirstUse);
        Assert.Equal(-1, work.AdditionalFirstUse);
    }

    private static void AssertOutputs(ClusterPipelineOutputs expected, ClusterPipelineOutputs actual)
    {
        Assert.Equal(expected.Visibility, actual.Visibility);
        Assert.Equal(expected.Depth, actual.Depth);
        Assert.Equal(expected.MaterialColor, actual.MaterialColor);
        Assert.Equal(expected.MotionVectors, actual.MotionVectors);
        Assert.Equal(expected.SurfaceReceivers, actual.SurfaceReceivers);
        Assert.Equal(expected.AdditionalSurfaceReceivers, actual.AdditionalSurfaceReceivers);
        Assert.Same(expected.Geometry, actual.Geometry);
        Assert.Null(actual.SurfaceReceiverConsumers);
    }
}

internal sealed partial class FrameStageScene
{
    private bool _observeDepthReceiverWork;
    internal DepthReceiverFrame DepthReceiverWork { get; private set; } = new([], 0, -1, -1);
    internal bool UsesDepthReceiverRequests => _shadows.UsesPrimaryDepthReceiverRequests;

    internal void SetDepthReceiverRequests(bool enabled)
    {
        _shadows.SetPrimaryDepthReceiverRequests(enabled);
        _observeDepthReceiverWork = true;
    }

    private void ObserveDepthReceiverWork(in RenderGraphDiagnosticsView diagnostics)
    {
        if (!_observeDepthReceiverWork) return;
        RenderGraphPassDiagnostic[] scheduled = diagnostics.Passes.ToArray()
            .Where(pass => pass.Live && pass.ScheduledOrdinal >= 0).ToArray();
        var scheduledIds = scheduled.Select(pass => pass.Id).ToHashSet();
        RenderGraphBufferDiagnostic[] buffers = diagnostics.Buffers.ToArray();
        int primary = Array.FindIndex(buffers, item => item.Id == Outputs.SurfaceReceivers);
        int additional = Array.FindIndex(buffers, item => item.Id == Outputs.AdditionalSurfaceReceivers);
        Assert.True(primary >= 0 && additional >= 0);
        int accesses = diagnostics.Accesses.ToArray().Count(access =>
            access.TargetKind == GraphAccessTargetKind.Buffer &&
            (access.TargetOrdinal == primary || access.TargetOrdinal == additional) &&
            scheduledIds.Contains(access.Pass));
        DepthReceiverWork = new(
            scheduled.Where(pass => pass.Label.StartsWith("Evaluate Cluster receivers bin ", StringComparison.Ordinal))
                .Select(pass => pass.Label).ToArray(),
            accesses, buffers[primary].FirstScheduledUse, buffers[additional].FirstScheduledUse);
    }
}

internal sealed record DepthReceiverFrame(string[] ReceiverPasses, int ReceiverAccessCount,
    int PrimaryFirstUse, int AdditionalFirstUse);
