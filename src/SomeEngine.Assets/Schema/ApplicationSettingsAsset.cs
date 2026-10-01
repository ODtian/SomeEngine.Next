using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.RuntimeConfiguration")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ApplicationSettingsAsset : Asset
{
    public const string FileType = "SomeEngine.Assets.Schema.RuntimeConfiguration";
    public const string PathSuffix = ".runtime.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<ApplicationSettingsAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public ApplicationSettingsAsset() : this(Guid.Empty) { }
    public ApplicationSettingsAsset(Guid id = default) : base(id)
    {
        try
        {
            AssetType = FileType;
            Name = "SomeEngine";
            using var settings = new ApplicationSettingsBlock();
            SetBlock(settings);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private ApplicationSettingsAsset(ApplicationSettingsAsset source) : base(source) { }
    public override ApplicationSettingsAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override ApplicationSettingsAsset CreateCopy(Guid id = default) => (ApplicationSettingsAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using ApplicationSettingsAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ApplicationSettingsAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new ApplicationSettingsAsset(description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            if (result.ContainsBlock<ApplicationSettingsBlock>())
                await result.GetBlockAsync<ApplicationSettingsBlock>(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            try { result.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public static async ValueTask<ApplicationSettingsAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ApplicationSettingsAsset> OpenAsync(AssetLoader loader, AssetId<ApplicationSettingsAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }


}
