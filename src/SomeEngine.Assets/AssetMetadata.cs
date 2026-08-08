namespace SomeEngine.Assets;

internal readonly record struct AssetDescription(
    AssetGuid AssetGuid,
    string Name,
    string AssetType,
    ulong SchemaFingerprint,
    IReadOnlyList<AssetGuid> Dependencies);

internal static class AssetMetadata
{
    internal static ulong RawBytesTypeFingerprint { get; }
        = SomeEngine.Serialization.BinaryFieldKey.FromName("SomeEngine.Assets.RawBytes.v1");

    internal static AssetDescription Describe<T>(T asset, string path)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        if (!descriptor.MatchesPath(path))
        {
            throw new InvalidDataException(
                $"Asset type '{descriptor.AssetType}' does not accept path '{path}'.");
        }

        return new AssetDescription(
            descriptor.GetAssetGuid(asset),
            descriptor.GetName(asset),
            descriptor.AssetType,
            descriptor.SchemaFingerprint,
            descriptor.GetDependencies(asset, path));
    }
}
