namespace SomeEngine.Assets.Importing;

/// <summary>Identity, output location and asset creation for one kind of source element.</summary>
public abstract class AssetSource<TSource, TAsset> where TSource : class where TAsset : Asset
{
    public abstract string GetElementKey(TSource source);
    public abstract TAsset CreateAsset(TSource source, Guid id);
    public abstract string GetOutputPath(TSource source, Guid id);
    public virtual ValueTask<AssetEntry> SaveAsync(TAsset asset, string path, CancellationToken cancellationToken = default)
        => asset.SaveAsync(path, cancellationToken);
}
