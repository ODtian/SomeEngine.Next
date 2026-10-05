using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.VirtualShadowMapShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(ResetReceiverMasks), nameof(ResetFrame), nameof(MarkPages), nameof(MarkCoarsePages), nameof(AllocatePages), nameof(ClearPages), nameof(InitializeState), nameof(InvalidateCasters), nameof(BuildPagePool), nameof(CompactViews), nameof(InitializePageHiZ), nameof(ReducePageHiZ), nameof(DynamicResolution), nameof(CompletePageDepth))]
public sealed partial class VirtualShadowMapShadersBlock : AssetBlock
{
    public partial ShaderRef? ResetReceiverMasks { get; set; }
    public partial ShaderRef? ResetFrame { get; set; }
    public partial ShaderRef? MarkPages { get; set; }
    public partial ShaderRef? MarkCoarsePages { get; set; }
    public partial ShaderRef? AllocatePages { get; set; }
    public partial ShaderRef? ClearPages { get; set; }
    public partial ShaderRef? InitializeState { get; set; }
    public partial ShaderRef? InvalidateCasters { get; set; }
    public partial ShaderRef? BuildPagePool { get; set; }
    public partial ShaderRef? CompactViews { get; set; }
    public partial ShaderRef? InitializePageHiZ { get; set; }
    public partial ShaderRef? ReducePageHiZ { get; set; }
    public partial ShaderRef? DynamicResolution { get; set; }
    public partial ShaderRef? CompletePageDepth { get; set; }

    protected override void ValidateContent() => Validate("shader settings");

    public async ValueTask<VirtualShadowMapShaders> CreateAsync(
        string name,
        AssetLoader loader,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        using VirtualShadowMapShadersBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        copy.Validate(name);
        Dictionary<Guid, Shader> shaders = await ShaderRef.LoadAsync(
            copy.References(),
            loader,
            slang,
            sessionDescription,
            specializations,
            cancellationToken).ConfigureAwait(false);
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
            Name = name,
            ResetReceiverMasksShader = shaders[copy.ResetReceiverMasks!.Asset.Value],
            ResetReceiverMasks = Resolve(copy.ResetReceiverMasks),
            ResetFrameShader = shaders[copy.ResetFrame!.Asset.Value],
            ResetFrame = Resolve(copy.ResetFrame),
            MarkPagesShader = shaders[copy.MarkPages!.Asset.Value],
            MarkPages = Resolve(copy.MarkPages),
            MarkCoarsePagesShader = shaders[copy.MarkCoarsePages!.Asset.Value],
            MarkCoarsePages = Resolve(copy.MarkCoarsePages),
            AllocatePagesShader = shaders[copy.AllocatePages!.Asset.Value],
            AllocatePages = Resolve(copy.AllocatePages),
            ClearPagesShader = shaders[copy.ClearPages!.Asset.Value],
            ClearPages = Resolve(copy.ClearPages),
            InitializeStateShader = shaders[copy.InitializeState!.Asset.Value],
            InitializeState = Resolve(copy.InitializeState),
            InvalidateCastersShader = shaders[copy.InvalidateCasters!.Asset.Value],
            InvalidateCasters = Resolve(copy.InvalidateCasters),
            BuildPagePoolShader = shaders[copy.BuildPagePool!.Asset.Value],
            BuildPagePool = Resolve(copy.BuildPagePool),
            CompactViewsShader = shaders[copy.CompactViews!.Asset.Value],
            CompactViews = Resolve(copy.CompactViews),
            InitializePageHiZShader = shaders[copy.InitializePageHiZ!.Asset.Value],
            InitializePageHiZ = Resolve(copy.InitializePageHiZ),
            ReducePageHiZShader = shaders[copy.ReducePageHiZ!.Asset.Value],
            ReducePageHiZ = Resolve(copy.ReducePageHiZ),
            CompletePageDepthShader = shaders[copy.CompletePageDepth!.Asset.Value],
            CompletePageDepth = Resolve(copy.CompletePageDepth),
            DynamicResolutionShader = shaders[copy.DynamicResolution!.Asset.Value],
            DynamicResolution = Resolve(copy.DynamicResolution),
        };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
