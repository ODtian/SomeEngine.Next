using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;

using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using Game.Content;
namespace Game.Assets;

public sealed class UnlitMaterialAsset : MaterialAsset
{
    public UnlitMaterialAsset() : this(Guid.Empty) { }
    public UnlitMaterialAsset(Guid id = default) : base(id, UnlitMaterial.TypeId) { }
    private UnlitMaterialAsset(UnlitMaterialAsset source) : base(source) { }
    public override UnlitMaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public override UnlitMaterialAsset CreateCopy(Guid id = default) => (UnlitMaterialAsset)base.CreateCopy(id);
    public new static ValueTask<UnlitMaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default) => OpenTypedAsync<UnlitMaterialAsset>(loader, id, cancellationToken);
    public new static ValueTask<UnlitMaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default) => OpenTypedAsync<UnlitMaterialAsset>(path, cancellationToken);
    public new async ValueTask<UnlitMaterial> CreateAsync(AssetLoader loader, CancellationToken cancellationToken = default)
        => (UnlitMaterial)await base.CreateAsync(loader, cancellationToken).ConfigureAwait(false);
}
