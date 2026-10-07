using SlangShaderSharp;

namespace SomeEngine.Resources;

/// <summary>Ready shadow-map shaders and their direct Slang entry points.</summary>
public sealed class VirtualShadowMapShaders : IDisposable
{
    /// <summary>Owned blue-noise pixels; GPU data uses the existing texture device path.</summary>
    public Texture? BlueNoise { get; init; }
    public string? Name { get; init; }
    public Shader? ProjectLightsShader { get; init; }
    public EntryPointReflection ProjectLights { get; init; }
    public Shader? FroxelBuildShader { get; init; }
    public EntryPointReflection FroxelBuild { get; init; }
    public Shader? FroxelArgumentsShader { get; init; }
    public EntryPointReflection FroxelArguments { get; init; }
    public Shader? FroxelMarkShader { get; init; }
    public EntryPointReflection FroxelMark { get; init; }
    public Shader? FroxelPixelShader { get; init; }
    public EntryPointReflection FroxelPixel { get; init; }
    public Shader? ResetReceiverMasksShader { get; init; }
    public EntryPointReflection ResetReceiverMasks { get; init; }
    public Shader? ResetFrameShader { get; init; }
    public EntryPointReflection ResetFrame { get; init; }
    public Shader? MarkPagesShader { get; init; }
    public EntryPointReflection MarkPages { get; init; }
    public Shader? MarkCoarsePagesShader { get; init; }
    public EntryPointReflection MarkCoarsePages { get; init; }
    public Shader? AllocatePagesShader { get; init; }
    public EntryPointReflection AllocatePages { get; init; }
    public Shader? ResidentPagesShader { get; init; }
    public EntryPointReflection ResidentPages { get; init; }
    public Shader? PrefilterShader { get; init; }
    public EntryPointReflection Prefilter { get; init; }
    public Shader? PrefilterRequestsShader { get; init; }
    public EntryPointReflection PrefilterRequests { get; init; }
    public Shader? MergeStaticShader { get; init; }
    public EntryPointReflection MergeStatic { get; init; }
    public Shader? ClearPagesShader { get; init; }
    public EntryPointReflection ClearPages { get; init; }
    public Shader? InitializeStateShader { get; init; }
    public EntryPointReflection InitializeState { get; init; }
    public Shader? InvalidateCastersShader { get; init; }
    public EntryPointReflection InvalidateCasters { get; init; }
    public Shader? BuildPagePoolShader { get; init; }
    public EntryPointReflection BuildPagePool { get; init; }
    public Shader? CompactViewsShader { get; init; }
    public EntryPointReflection CompactViews { get; init; }
    public Shader? InitializePageHiZShader { get; init; }
    public EntryPointReflection InitializePageHiZ { get; init; }
    public Shader? ReducePageHiZShader { get; init; }
    public EntryPointReflection ReducePageHiZ { get; init; }
    public Shader? CompletePageDepthShader { get; init; }
    public EntryPointReflection CompletePageDepth { get; init; }
    public Shader? WorkFeedbackShader { get; init; }
    public EntryPointReflection WorkFeedback { get; init; }
    public Shader? DynamicResolutionShader { get; init; }
    public EntryPointReflection DynamicResolution { get; init; }

    public void Dispose() => BlueNoise?.Dispose();
}
