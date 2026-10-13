using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;

using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using Game.Content;
namespace Game.Assets;

/// <summary>A standard material identity and its typed creation functions.</summary>
public sealed class StandardMaterialAsset : MaterialAsset
{
    public StandardMaterialAsset() : this(Guid.Empty) { }
    public StandardMaterialAsset(Guid id = default) : base(id, StandardMaterial.TypeId) { }
    private StandardMaterialAsset(StandardMaterialAsset source) : base(source) { }
    public override StandardMaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public override StandardMaterialAsset CreateCopy(Guid id = default) => (StandardMaterialAsset)base.CreateCopy(id);

    public new static ValueTask<StandardMaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default) => OpenTypedAsync<StandardMaterialAsset>(loader, id, cancellationToken);
    public new static ValueTask<StandardMaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default) => OpenTypedAsync<StandardMaterialAsset>(path, cancellationToken);
    public new async ValueTask<StandardMaterial> CreateAsync(AssetLoader loader, CancellationToken cancellationToken = default)
        => (StandardMaterial)await base.CreateAsync(loader, cancellationToken).ConfigureAwait(false);
}
