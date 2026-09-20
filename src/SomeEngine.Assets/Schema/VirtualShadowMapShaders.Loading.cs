namespace SomeEngine.Assets.Schema;

[Asset(".virtualshadow.asset")]
public sealed partial class VirtualShadowMapShaders : IDisposable
{
    private readonly Dictionary<Guid, Shader> _shaders = [];
    public void Dispose()
    {
        Shader[] shaders = [.. _shaders.Values];
        _shaders.Clear();
        var errors = new List<Exception>();
        foreach (Shader shader in shaders)
            try { shader.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0)
            throw new AggregateException("Some virtual shadow map shaders could not be closed.", errors);
    }
    public partial string? Name { get; set; }
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
    public Shader GetShader(ShaderRef reference) => _shaders[Guid.Parse(reference.AssetGuid!)];
    public static async ValueTask<VirtualShadowMapShaders> CreateAsync(VirtualShadowMapShaders.Asset asset, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        VirtualShadowMapShadersData fields = asset.CopyValues();
        fields.Validate(asset.SourcePath);
        var result = new VirtualShadowMapShaders
        {
            Name = fields.Name,
            MarkPages = fields.MarkPages,
            MarkCoarsePages = fields.MarkCoarsePages,
            AllocatePages = fields.AllocatePages,
            ClearPages = fields.ClearPages,
            InitializeState = fields.InitializeState,
            InvalidateCasters = fields.InvalidateCasters,
            BuildPagePool = fields.BuildPagePool,
            CompactViews = fields.CompactViews,
            InitializePageHiZ = fields.InitializePageHiZ,
            ReducePageHiZ = fields.ReducePageHiZ,
        };
        try
        {
            foreach (Guid id in asset.Describe().Dependencies)
            {
                await using Shader.Asset shader = await Shader.Asset.OpenAsync(loader, new AssetId<Shader>(id), cancellationToken).ConfigureAwait(false);
                result._shaders.Add(id, await Shader.CreateAsync(shader, cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception failure)
        {
            try { result.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }
}
