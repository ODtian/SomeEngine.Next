using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

/// <summary>A standard material identity and its typed creation functions.</summary>
public sealed class StandardMaterialAsset : MaterialAsset
{
    public StandardMaterialAsset() : this(Guid.Empty) { }
    public StandardMaterialAsset(Guid id = default) : base(id) { }
    private StandardMaterialAsset(StandardMaterialAsset source) : base(source) { }
    public override StandardMaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public override StandardMaterialAsset CreateCopy(Guid id = default) => (StandardMaterialAsset)base.CreateCopy(id);

    public new static ValueTask<StandardMaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default) => OpenTypedAsync<StandardMaterialAsset>(loader, id, cancellationToken);
    public new static ValueTask<StandardMaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default) => OpenTypedAsync<StandardMaterialAsset>(path, cancellationToken);
    public ValueTask<StandardMaterial> CreateAsync(AssetLoader loader, CancellationToken cancellationToken = default)
        => CreateAsync<StandardMaterial>(loader, cancellationToken);
}
