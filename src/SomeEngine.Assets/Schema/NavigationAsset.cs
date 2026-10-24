using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>One navigation dataset identity with independently selected representation blocks.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Navigation")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class NavigationAsset : Asset
{
    public const string FileType = "SomeEngine.Assets.Schema.Navigation";
    public const string PathSuffix = ".navigation.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<NavigationAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public NavigationAsset() : this(Guid.Empty) { }
    public NavigationAsset(Guid id = default) : base(id) => AssetType = FileType;
    private NavigationAsset(NavigationAsset source) : base(source) { }
    public override NavigationAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationAsset CreateCopy(Guid id = default) => (NavigationAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using NavigationAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static NavigationAsset ReadFile(AssetFile file, Guid expected)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new NavigationAsset(description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static async ValueTask<NavigationAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadFile(file, Guid.Empty);
    }

    public static async ValueTask<NavigationAsset> OpenAsync(
        AssetLoader loader, AssetId<NavigationAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return ReadFile(file, id.Value);
    }
}
