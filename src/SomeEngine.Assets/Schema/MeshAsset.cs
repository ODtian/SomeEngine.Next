using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>A mesh identity with independently encoded geometry and pipeline representations.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Mesh")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class MeshAsset : Asset
{
    public const string FileType = "SomeEngine.Assets.Schema.Mesh";
    public const string PathSuffix = ".mesh.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<MeshAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public MeshAsset() : this(Guid.Empty) { }

    public MeshAsset(Guid id = default) : base(id)
    {
        AssetType = FileType;
    }

    private MeshAsset(MeshAsset source) : base(source) { }
    public override MeshAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override MeshAsset CreateCopy(Guid id = default) => (MeshAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using MeshAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<MeshAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new MeshAsset(description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            // These are small metadata blocks. Geometry pages stay in the document.
            if (result.ContainsBlock<MeshBlock>())
                await result.GetBlockAsync<MeshBlock>(cancellationToken).ConfigureAwait(false);
            if (result.ContainsBlock<ClusterGeometryBlock>())
                await result.GetBlockAsync<ClusterGeometryBlock>(cancellationToken).ConfigureAwait(false);
            if (result.ContainsBlock<MeshMaterialsBlock>())
                await result.GetBlockAsync<MeshMaterialsBlock>(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static async ValueTask<MeshAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<MeshAsset> OpenAsync(AssetLoader loader, AssetId<MeshAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }


}
