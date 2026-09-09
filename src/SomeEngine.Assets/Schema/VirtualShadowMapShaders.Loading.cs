namespace SomeEngine.Assets.Schema;

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
    public string? Name { get; set; }
    public ShaderRef? MarkPages { get; set; }
    public ShaderRef? MarkCoarsePages { get; set; }
    public ShaderRef? AllocatePages { get; set; }
    public ShaderRef? ClearPages { get; set; }
    public ShaderRef? InitializeState { get; set; }
    public ShaderRef? InvalidateCasters { get; set; }
    public ShaderRef? BuildPagePool { get; set; }
    public ShaderRef? CompactViews { get; set; }
    public ShaderRef? InitializePageHiZ { get; set; }
    public ShaderRef? ReducePageHiZ { get; set; }
    public Shader GetShader(ShaderRef reference) => _shaders[Guid.Parse(reference.AssetGuid!)];
    public static async ValueTask<VirtualShadowMapShaders> CreateAsync(Asset<VirtualShadowMapShaders> asset, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        VirtualShadowMapShadersData fields = asset.GetFieldsGenerated<VirtualShadowMapShadersData>();
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
            foreach (Guid id in asset.Dependencies)
            {
                await using Asset<Shader> shader = await loader.OpenAsync(new AssetId<Shader>(id), cancellationToken).ConfigureAwait(false);
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
