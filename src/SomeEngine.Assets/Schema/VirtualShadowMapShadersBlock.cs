using SlangShaderSharp;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.VirtualShadowMapShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(ResetReceiverMasks), nameof(ResetFrame), nameof(MarkPages), nameof(MarkCoarsePages), nameof(AllocatePages), nameof(ClearPages), nameof(InitializeState), nameof(InvalidateCasters), nameof(BuildPagePool), nameof(CompactViews), nameof(InitializePageHiZ), nameof(ReducePageHiZ), nameof(DynamicResolution))]
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

    protected override void ValidateContent() => Validate("shader settings");

    public VirtualShadowMapShaders Create(
        string name,
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders)
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
            Name = name,
            ResetReceiverMasksShader = shaders[copy.ResetReceiverMasks!.Asset],
            ResetReceiverMasks = Resolve(copy.ResetReceiverMasks),
            ResetFrameShader = shaders[copy.ResetFrame!.Asset],
            ResetFrame = Resolve(copy.ResetFrame),
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
            DynamicResolutionShader = shaders[copy.DynamicResolution!.Asset],
            DynamicResolution = Resolve(copy.DynamicResolution),
        };
        return result;
    }
}
