using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>An asset containing one independently loadable entity group.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.EntityFile")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class EntityAsset : Asset
{
    public const string FileType = "SomeEngine.ECS.Entities";
    public const string PathSuffix = ".entities.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<EntityAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public EntityAsset() : this(Guid.Empty) { }
    public EntityAsset(Guid id)
        : base(id)
    {
        AssetType = FileType;
        using var entities = new EntityBlock();
        SetBlock(entities);
    }

    private EntityAsset(EntityAsset source) : base(source) { }
    public override EntityAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override EntityAsset CreateCopy(Guid id = default) => (EntityAsset)base.CreateCopy(id);
    public AssetEntry Describe() => Describe(SchemaHash, BlockReferences());

    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using EntityAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<EntityAsset> OpenAsync(AssetLoader loader, AssetId<EntityAsset> id,
        CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<EntityAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The entity asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<EntityAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry entry = file.Describe();
        if ((expected != Guid.Empty && entry.AssetGuid != expected) || entry.AssetType != FileType)
            throw new InvalidDataException("The entity asset file has a different identity or type.");
        var asset = new EntityAsset(entry.AssetGuid);
        try
        {
            asset.ReadBlockContent(file);
            asset.AttachFile(file);
            EntityBlock entities = await asset.GetBlockAsync<EntityBlock>(cancellationToken).ConfigureAwait(false);
            if (entities.ContentLength == 0) throw new InvalidDataException("The entity asset requires encoded content.");
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
