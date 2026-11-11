using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>A scene identity and its directory of independently loadable entity content.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.WorldFile")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class WorldAsset : Asset
{
    public const string FileType = "SomeEngine.ECS.World";
    public const string PathSuffix = ".scene.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<WorldAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
    public WorldAsset() : this(Guid.Empty) { }
    public WorldAsset(Guid id = default) : base(id)
    {
        AssetType = FileType;
        using var world = new WorldBlock();
        SetBlock(world);
    }
    private WorldAsset(WorldAsset source) : base(source) { }
    public override WorldAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override WorldAsset CreateCopy(Guid id = default) => (WorldAsset)base.CreateCopy(id);
    public AssetEntry Describe() => Describe(SchemaHash, BlockReferences());
    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using WorldAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<WorldAsset> OpenAsync(AssetLoader loader, AssetId<WorldAsset> id,
        CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<WorldAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The World path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, default, cancellationToken).ConfigureAwait(false);
    }
    private static async ValueTask<WorldAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry entry = file.Describe();
        if ((expected != Guid.Empty && entry.AssetGuid != expected) || entry.AssetType != FileType)
            throw new InvalidDataException("The World file has a different identity or type.");
        var asset = new WorldAsset(entry.AssetGuid);
        try
        {
            asset.ReadBlockContent(file);
            asset.AttachFile(file);
            await asset.GetBlockAsync<WorldBlock>(cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
