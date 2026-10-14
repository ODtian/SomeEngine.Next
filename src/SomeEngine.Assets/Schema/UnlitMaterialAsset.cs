using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

public sealed class UnlitMaterialAsset : MaterialAsset
{
    public UnlitMaterialAsset() : this(Guid.Empty) { }
    public UnlitMaterialAsset(Guid id = default) : base(id) { }
    private UnlitMaterialAsset(UnlitMaterialAsset source) : base(source) { }
    public override UnlitMaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public override UnlitMaterialAsset CreateCopy(Guid id = default) => (UnlitMaterialAsset)base.CreateCopy(id);
    public new static ValueTask<UnlitMaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default) => OpenTypedAsync<UnlitMaterialAsset>(loader, id, cancellationToken);
    public new static ValueTask<UnlitMaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default) => OpenTypedAsync<UnlitMaterialAsset>(path, cancellationToken);
    public ValueTask<UnlitMaterial> CreateAsync(AssetLoader loader, CancellationToken cancellationToken = default)
        => CreateAsync<UnlitMaterial>(loader, cancellationToken);
}
