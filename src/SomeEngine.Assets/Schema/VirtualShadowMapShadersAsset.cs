using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.VirtualShadowMapShaders")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class VirtualShadowMapShadersAsset : Asset
{
    public const string FileType = "SomeEngine.Assets.Schema.VirtualShadowMapShaders";
    public const string PathSuffix = ".virtualshadow.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<VirtualShadowMapShadersAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public VirtualShadowMapShadersAsset() : this(Guid.Empty) { }
    public VirtualShadowMapShadersAsset(Guid id = default) : base(id)
    {
        try
        {
            AssetType = FileType;
            using var settings = new VirtualShadowMapShadersBlock();
            SetBlock(settings);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private VirtualShadowMapShadersAsset(VirtualShadowMapShadersAsset source) : base(source) { }
    public override VirtualShadowMapShadersAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override VirtualShadowMapShadersAsset CreateCopy(Guid id = default) => (VirtualShadowMapShadersAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using VirtualShadowMapShadersAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<VirtualShadowMapShadersAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new VirtualShadowMapShadersAsset(description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            if (result.ContainsBlock<VirtualShadowMapShadersBlock>())
            {
                VirtualShadowMapShadersBlock shaders = await result.GetBlockAsync<VirtualShadowMapShadersBlock>(cancellationToken).ConfigureAwait(false);
                shaders.Validate(description.Name);
            }
            return result;
        }
        catch (Exception error)
        {
            try { result.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public static async ValueTask<VirtualShadowMapShadersAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<VirtualShadowMapShadersAsset> OpenAsync(AssetLoader loader, AssetId<VirtualShadowMapShadersAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }


}
