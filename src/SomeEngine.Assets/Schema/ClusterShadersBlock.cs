using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.ClusterShadersBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Operations))]
public sealed partial class ClusterShadersBlock : AssetPageBlock
{
    public partial IList<ClusterShaderOperation> Operations { get; set; }

    protected override void ValidateContent() => Validate("shader settings");

    public async ValueTask<ClusterShaders> CreateAsync(string name, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        using ClusterShadersBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        copy.Validate(name);
        Dictionary<Guid, Shader> shaders = await ShaderRef.LoadAsync(copy.References(), loader, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new ClusterShaders
            {
                Name = name,
                Operations = copy.Operations.Select(operation => new ShaderOperation
                {
                    Role = operation.Role,
                    Shaders = operation.Shaders!.Select(reference => reference.Resolve(shaders)).ToImmutableList(),
                }).ToImmutableList(),
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
            if (errors.Count > 1) throw new AggregateException("Creating cluster shaders and releasing resources failed.", errors);
            throw;
        }
    }
}
