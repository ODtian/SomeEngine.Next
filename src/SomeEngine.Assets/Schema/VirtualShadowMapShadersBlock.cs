using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.VirtualShadowMapShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(ResetReceiverMasks), nameof(ResetFrame), nameof(MarkPages), nameof(MarkCoarsePages), nameof(AllocatePages), nameof(ClearPages), nameof(InitializeState), nameof(InvalidateCasters), nameof(BuildPagePool), nameof(CompactViews), nameof(InitializePageHiZ), nameof(ReducePageHiZ), nameof(DynamicResolution), nameof(CompletePageDepth), nameof(WorkFeedback), nameof(MergeStatic), nameof(ResidentPages), nameof(Prefilter), nameof(PrefilterRequests), nameof(PrefilterReset), nameof(PrefilterSelect), nameof(BlueNoise), nameof(FroxelBuild), nameof(FroxelArguments), nameof(FroxelMark), nameof(FroxelPixel), nameof(ProjectDirectionalLights), nameof(ProjectLocalLights), nameof(MarkDepthPages))]
public sealed partial class VirtualShadowMapShadersBlock : AssetBlock
{
    public partial ShaderRef? ProjectDirectionalLights { get; set; }
    public partial ShaderRef? ProjectLocalLights { get; set; }
    public partial ShaderRef? FroxelBuild { get; set; }
    public partial ShaderRef? FroxelArguments { get; set; }
    public partial ShaderRef? FroxelMark { get; set; }
    public partial ShaderRef? FroxelPixel { get; set; }
    public partial AssetId<TextureAsset>? BlueNoise { get; set; }
    public partial ShaderRef? ResetReceiverMasks { get; set; }
    public partial ShaderRef? ResetFrame { get; set; }
    public partial ShaderRef? MarkDepthPages { get; set; }
    public partial ShaderRef? MarkPages { get; set; }
    public partial ShaderRef? MarkCoarsePages { get; set; }
    public partial ShaderRef? AllocatePages { get; set; }
    public partial ShaderRef? ResidentPages { get; set; }
    public partial ShaderRef? Prefilter { get; set; }
    public partial ShaderRef? PrefilterRequests { get; set; }
    public partial ShaderRef? PrefilterReset { get; set; }
    public partial ShaderRef? PrefilterSelect { get; set; }
    public partial ShaderRef? MergeStatic { get; set; }
    public partial ShaderRef? ClearPages { get; set; }
    public partial ShaderRef? InitializeState { get; set; }
    public partial ShaderRef? InvalidateCasters { get; set; }
    public partial ShaderRef? BuildPagePool { get; set; }
    public partial ShaderRef? CompactViews { get; set; }
    public partial ShaderRef? InitializePageHiZ { get; set; }
    public partial ShaderRef? ReducePageHiZ { get; set; }
    public partial ShaderRef? WorkFeedback { get; set; }
    public partial ShaderRef? DynamicResolution { get; set; }
    public partial ShaderRef? CompletePageDepth { get; set; }

    protected override void ValidateContent() => Validate("shader settings");

    public VirtualShadowMapShaders Create(string name,
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders,
        SomeEngine.Resources.Texture? blueNoise = null)
    {
        using VirtualShadowMapShadersBlock copy = Duplicate();
        copy.Validate(name);
        EntryPointReflection Resolve(ShaderRef reference)
        {
            EntryPointReflection entry = reference.ResolveEntry(shaders);
            if (entry.Stage != SlangStage.Compute)
            {
                throw new InvalidDataException(
                    $"Virtual shadow entry '{reference.EntryPoint}' is {entry.Stage}, not compute.");
            }
            return entry;
        }

            var result = new VirtualShadowMapShaders
            {
                BlueNoise = blueNoise,
                Name = name,
                ProjectDirectionalLightsShader = shaders[copy.ProjectDirectionalLights!.Asset],
                ProjectDirectionalLights = Resolve(copy.ProjectDirectionalLights),
                ProjectLocalLightsShader = shaders[copy.ProjectLocalLights!.Asset],
                ProjectLocalLights = Resolve(copy.ProjectLocalLights),
                FroxelBuildShader = copy.FroxelBuild is { } froxelBuild ? shaders[froxelBuild.Asset] : null,
                FroxelBuild = copy.FroxelBuild is { } froxelBuildEntry ? Resolve(froxelBuildEntry) : EntryPointReflection.Null,
                FroxelArgumentsShader = copy.FroxelArguments is { } froxelArguments ? shaders[froxelArguments.Asset] : null,
                FroxelArguments = copy.FroxelArguments is { } froxelArgumentsEntry ? Resolve(froxelArgumentsEntry) : EntryPointReflection.Null,
                FroxelMarkShader = copy.FroxelMark is { } froxelMark ? shaders[froxelMark.Asset] : null,
                FroxelMark = copy.FroxelMark is { } froxelMarkEntry ? Resolve(froxelMarkEntry) : EntryPointReflection.Null,
                FroxelPixelShader = copy.FroxelPixel is { } froxelPixel ? shaders[froxelPixel.Asset] : null,
                FroxelPixel = copy.FroxelPixel is { } froxelPixelEntry ? Resolve(froxelPixelEntry) : EntryPointReflection.Null,
                PrefilterRequestsShader = copy.PrefilterRequests is { } filterRequests ? shaders[filterRequests.Asset] : null,
                PrefilterRequests = copy.PrefilterRequests is { } filterRequestsEntry ? Resolve(filterRequestsEntry) : EntryPointReflection.Null,
                PrefilterResetShader = copy.PrefilterReset is { } filterReset ? shaders[filterReset.Asset] : null,
                PrefilterReset = copy.PrefilterReset is { } filterResetEntry ? Resolve(filterResetEntry) : EntryPointReflection.Null,
                PrefilterSelectShader = copy.PrefilterSelect is { } filterSelect ? shaders[filterSelect.Asset] : null,
                PrefilterSelect = copy.PrefilterSelect is { } filterSelectEntry ? Resolve(filterSelectEntry) : EntryPointReflection.Null,
                PrefilterShader = copy.Prefilter is { } prefilter ? shaders[prefilter.Asset] : null,
                Prefilter = copy.Prefilter is { } prefilterEntry ? Resolve(prefilterEntry) : EntryPointReflection.Null,
                ResidentPagesShader = copy.ResidentPages is { } resident ? shaders[resident.Asset] : null,
                ResidentPages = copy.ResidentPages is { } residentEntry ? Resolve(residentEntry) : EntryPointReflection.Null,
                MergeStaticShader = copy.MergeStatic is { } merge ? shaders[merge.Asset] : null,
                MergeStatic = copy.MergeStatic is { } mergeEntry ? Resolve(mergeEntry) : EntryPointReflection.Null,
                WorkFeedbackShader = copy.WorkFeedback is { } work ? shaders[work.Asset] : null,
                WorkFeedback = copy.WorkFeedback is { } workEntry ? Resolve(workEntry) : EntryPointReflection.Null,
                ResetReceiverMasksShader = shaders[copy.ResetReceiverMasks!.Asset],
                ResetReceiverMasks = Resolve(copy.ResetReceiverMasks),
                ResetFrameShader = shaders[copy.ResetFrame!.Asset],
                ResetFrame = Resolve(copy.ResetFrame),
                MarkDepthPagesShader = copy.MarkDepthPages is { } depthPages ? shaders[depthPages.Asset] : null,
                MarkDepthPages = copy.MarkDepthPages is { } depthPagesEntry ? Resolve(depthPagesEntry) : EntryPointReflection.Null,
                MarkPagesShader = shaders[copy.MarkPages!.Asset],
                MarkPages = Resolve(copy.MarkPages),
                MarkCoarsePagesShader = shaders[copy.MarkCoarsePages!.Asset],
                MarkCoarsePages = Resolve(copy.MarkCoarsePages),
                AllocatePagesShader = shaders[copy.AllocatePages!.Asset],
                AllocatePages = Resolve(copy.AllocatePages),
                ClearPagesShader = shaders[copy.ClearPages!.Asset],
                ClearPages = Resolve(copy.ClearPages),
                InitializeStateShader = shaders[copy.InitializeState!.Asset],
                InitializeState = Resolve(copy.InitializeState),
                InvalidateCastersShader = shaders[copy.InvalidateCasters!.Asset],
                InvalidateCasters = Resolve(copy.InvalidateCasters),
                BuildPagePoolShader = shaders[copy.BuildPagePool!.Asset],
                BuildPagePool = Resolve(copy.BuildPagePool),
                CompactViewsShader = shaders[copy.CompactViews!.Asset],
                CompactViews = Resolve(copy.CompactViews),
                InitializePageHiZShader = shaders[copy.InitializePageHiZ!.Asset],
                InitializePageHiZ = Resolve(copy.InitializePageHiZ),
                ReducePageHiZShader = shaders[copy.ReducePageHiZ!.Asset],
                ReducePageHiZ = Resolve(copy.ReducePageHiZ),
                CompletePageDepthShader = shaders[copy.CompletePageDepth!.Asset],
                CompletePageDepth = Resolve(copy.CompletePageDepth),
                DynamicResolutionShader = shaders[copy.DynamicResolution!.Asset],
                DynamicResolution = Resolve(copy.DynamicResolution),
            };
        return result;
    }

    public IEnumerable<Guid> GetShaderAssetIds()
    {
        ShaderRef?[] entries =
        [
            ResetReceiverMasks, ResetFrame, MarkPages, MarkDepthPages, MarkCoarsePages, AllocatePages, ClearPages,
            InitializeState, InvalidateCasters, BuildPagePool, CompactViews, InitializePageHiZ,
            ReducePageHiZ, DynamicResolution, CompletePageDepth, WorkFeedback, MergeStatic,
            ResidentPages, Prefilter, PrefilterRequests, PrefilterReset, PrefilterSelect, FroxelBuild, FroxelArguments, FroxelMark, FroxelPixel, ProjectDirectionalLights, ProjectLocalLights,
        ];
        return entries.OfType<ShaderRef>().Select(static reference => reference.Asset.Value);
    }
}
