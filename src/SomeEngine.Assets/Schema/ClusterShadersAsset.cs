using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.ClusterShaders")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ClusterShadersAsset : Asset
{
    public const string FileType = "SomeEngine.Assets.Schema.ClusterShaders";
    public const string PathSuffix = ".clusterrender.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<ClusterShadersAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public ClusterShadersAsset() : this(PageArena.Shared) { }
    public ClusterShadersAsset(PageArena pages, Guid id = default) : base(pages, id)
    {
        try
        {
            AssetType = FileType;
            using var settings = new ClusterShadersBlock(pages);
            SetBlock(settings);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private ClusterShadersAsset(ClusterShadersAsset source) : base(source) { }
    public override ClusterShadersAsset Duplicate() { lock (Access.SyncRoot) { RequireIdle(); return new(this); } }
    public override ClusterShadersAsset CreateCopy(Guid id = default) => (ClusterShadersAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using ClusterShadersAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ClusterShadersAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new ClusterShadersAsset(file.Pages, description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            if (result.ContainsBlock<ClusterShadersBlock>())
            {
                ClusterShadersBlock shaders = await result.GetBlockAsync<ClusterShadersBlock>(cancellationToken).ConfigureAwait(false);
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

    public static async ValueTask<ClusterShadersAsset> OpenAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, pages, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ClusterShadersAsset> OpenAsync(AssetLoader loader, AssetId<ClusterShadersAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }


}
