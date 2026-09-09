namespace SomeEngine.Assets.Schema;

public sealed partial class ClusterShaders : IDisposable
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
            throw new AggregateException("Some cluster shaders could not be closed.", errors);
    }
    public string? Name { get; set; }
    public IList<ClusterShaderOperation>? Operations { get; set; }
    public Shader GetShader(ShaderRef reference) => _shaders[Guid.Parse(reference.AssetGuid!)];
    public static async ValueTask<ClusterShaders> CreateAsync(Asset<ClusterShaders> asset, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        ClusterShadersData fields = asset.GetFieldsGenerated<ClusterShadersData>();
        var result = new ClusterShaders
        {
            Name = fields.Name,
            Operations = fields.Operations,
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
    public static bool IsPositionOperation(ClusterShaderOperationRole role) => role is ClusterShaderOperationRole.SoftwareVisibilityRaster or ClusterShaderOperationRole.HardwareVisibilityRaster;
}
