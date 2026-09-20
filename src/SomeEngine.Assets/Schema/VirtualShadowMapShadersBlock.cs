using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.VirtualShadowMapShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(ResetReceiverMasks), nameof(ResetFrame), nameof(DynamicResolution), nameof(MarkPages), nameof(MarkCoarsePages), nameof(AllocatePages), nameof(ClearPages), nameof(InitializeState), nameof(InvalidateCasters), nameof(BuildPagePool), nameof(CompactViews), nameof(InitializePageHiZ), nameof(ReducePageHiZ))]
public sealed partial class VirtualShadowMapShadersBlock : AssetPageBlock
{
    public partial ShaderRef? ResetReceiverMasks { get; set; }
    public partial ShaderRef? ResetFrame { get; set; }
    public partial ShaderRef? DynamicResolution { get; set; }
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

    protected override void ValidateContent() => Validate("shader settings");

    public async ValueTask<VirtualShadowMapShaders> CreateAsync(string name, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        using VirtualShadowMapShadersBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        copy.Validate(name);
        Dictionary<Guid, Shader> shaders = await ShaderRef.LoadAsync(copy.References(), loader, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new VirtualShadowMapShaders
            {
                Name = name,
                ResetFrame = copy.ResetFrame!.Resolve(shaders),
                ResetReceiverMasks = copy.ResetReceiverMasks!.Resolve(shaders),
                DynamicResolution = copy.DynamicResolution!.Resolve(shaders),
                MarkPages = copy.MarkPages!.Resolve(shaders),
                MarkCoarsePages = copy.MarkCoarsePages!.Resolve(shaders),
                AllocatePages = copy.AllocatePages!.Resolve(shaders),
                ClearPages = copy.ClearPages!.Resolve(shaders),
                InitializeState = copy.InitializeState!.Resolve(shaders),
                InvalidateCasters = copy.InvalidateCasters!.Resolve(shaders),
                BuildPagePool = copy.BuildPagePool!.Resolve(shaders),
                CompactViews = copy.CompactViews!.Resolve(shaders),
                InitializePageHiZ = copy.InitializePageHiZ!.Resolve(shaders),
                ReducePageHiZ = copy.ReducePageHiZ!.Resolve(shaders),
            };
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            foreach (Shader shader in shaders.Values)
            {
                try { shader.Dispose(); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (errors.Count > 1) throw new AggregateException("Creating shadow shaders and releasing resources failed.", errors);
            throw;
        }
    }
}
