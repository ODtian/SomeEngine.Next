using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowSubmissionRecoveryTests
{
    [Theory]
    [InlineData("D3D12", SceneLightKind.Point)]
    [InlineData("D3D12", SceneLightKind.Spot)]
    [InlineData("Vulkan", SceneLightKind.Point)]
    [InlineData("Vulkan", SceneLightKind.Spot)]
    public async Task FirstLocalShadowOutputAfterPartiallySubmittedCallbackFailureMatchesFreshScene(string backend, SceneLightKind light)
    {
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true, ForceHardwareRaster = true };
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, DynamicResolutionEnabled = false };
        await using var changed = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: settings);
        if (light == SceneLightKind.Spot) changed.UseSpotLight(new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
            MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30));
        await changed.ReadRasterImageAsync();
        changed.SetMeshTransform(1, new(new Vector3(-1, 0, -6), Quaternion.Identity, .6f));
        var result = new ExecutionResult();
        GraphPassId failure = changed.AddRecordingFailure(() => throw new UserCopyFailure(), result);
        await Assert.ThrowsAsync<UserCopyFailure>(() => changed.ReadNextRasterImageAsync());
        Assert.True(result.PartiallySubmitted, "The injected callback must fail after actual accepted work, not before submission.");
        changed.RemoveRecordingFailure(failure);
        RasterImage resumed = await changed.ReadNextRasterImageAsync();
        await using var fresh = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: settings);
        if (light == SceneLightKind.Spot) fresh.UseSpotLight(new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
            MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30));
        fresh.SetMeshTransform(1, new(new Vector3(-1, 0, -6), Quaternion.Identity, .6f));
        // Fresh content must reach ready texture/geometry uploads; the recovery
        // observation remains the very first frame after the actual failure.
        RasterImage expected = await fresh.ReadRasterImageAsync();
        RasterImage.AssertEqual(expected, resumed);
    }

    private sealed class UserCopyFailure : Exception;
    private sealed class ExecutionResult : IRenderGraphFrameExecutionObserver
    {
        internal bool PartiallySubmitted;
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) =>
            PartiallySubmitted = !result.SubmittedCompletions.IsEmpty;
    }
}
