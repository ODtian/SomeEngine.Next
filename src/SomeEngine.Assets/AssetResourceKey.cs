namespace SomeEngine.Assets;

/// <summary>The file source, asset identity and selected content version for one resource.</summary>
public readonly record struct AssetResourceKey<TAsset>(
    AssetLoader Files, AssetId<TAsset> Asset, Guid Version) where TAsset : Asset;
