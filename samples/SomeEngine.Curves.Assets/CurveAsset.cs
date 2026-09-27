using System.Collections.Immutable;
using SomeEngine.Assets;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Examples.Curves;

[AssetBlock, BinarySerializable, TextSerializable, BinaryFields(nameof(Points))]
public sealed partial class CurveBlock : AssetPageBlock
{
    public partial IList<Point> Points { get; set; }
    public Curve CreateCurve() => new(ReadPoints());
    public void Update(Curve curve) => curve.Update(ReadPoints());
    public CurveSamples CreateSamples(float start, float end, int count)
    {
        using var curve = CreateCurve();
        return new(curve, start, end, count);
    }
}

[AssetBlock, BinarySerializable, TextSerializable, BinaryFields(nameof(Value))]
public sealed partial class ConstantCurveBlock : AssetPageBlock
{
    public partial float Value { get; set; }
    public Curve CreateCurve()
    {
        using var points = new PageList<Point>(Pages);
        points.Add(new Point(0, Value));
        return new(points.Retain());
    }
}
[BinarySerializable, BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class CurveAsset : Asset
{
    public const string FileType = "SomeEngine.Examples.Curves.CurveAsset";
    public const string PathSuffix = ".curve.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<CurveAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
    public CurveAsset() : this(PageArena.Shared) { }
    public CurveAsset(PageArena pages, Guid id = default) : base(pages, id)
    {
        AssetType = FileType;
        using var content = new CurveBlock(pages);
        SetBlock(content);
    }
    private CurveAsset(CurveAsset source) : base(source) { }
    public override CurveAsset Duplicate() { lock (Access.SyncRoot) { RequireIdle(); return new(this); } }
    public override CurveAsset CreateCopy(Guid id = default) => (CurveAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());
    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using CurveAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<CurveAsset> OpenAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, pages, cancellationToken: cancellationToken).ConfigureAwait(false);
        AssetEntry entry = file.Describe();
        var asset = new CurveAsset(file.Pages, entry.AssetGuid);
        try
        {
            asset.ReadBlockContent(file);
            asset.AttachFile(file);
            await asset.GetBlockAsync<CurveBlock>(cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
    public ValueTask<AssetEntry> SaveSourceAsync(string path, CancellationToken cancellationToken = default)
        => AssetText.SaveAsync(path, this, static (writer, asset) => writer.WriteBlock(asset.GetBlock<CurveBlock>()), cancellationToken);
    public static async ValueTask<CurveAsset> OpenSourceAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        using AssetText.SourceReader source = await AssetText.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        source.RequireAsset<CurveAsset>(FileType);
        var asset = new CurveAsset(pages ?? PageArena.Shared, source.Description.AssetGuid) { Name = source.Description.Name };
        try { await source.ReadBlockIntoAsync(asset.GetBlock<CurveBlock>(), cancellationToken).ConfigureAwait(false); return asset; }
        catch { asset.Dispose(); throw; }
    }
}
[BinarySerializable, BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ConstantCurveAsset : Asset
{
    public const string FileType = "SomeEngine.Examples.Curves.ConstantCurveAsset";
    public const string PathSuffix = ".constant-curve.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<ConstantCurveAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
    public ConstantCurveAsset() : this(PageArena.Shared) { }
    public ConstantCurveAsset(PageArena pages, Guid id = default) : base(pages, id)
    {
        AssetType = FileType;
        using var content = new ConstantCurveBlock(pages);
        SetBlock(content);
    }
    private ConstantCurveAsset(ConstantCurveAsset source) : base(source) { }
    public override ConstantCurveAsset Duplicate() { lock (Access.SyncRoot) { RequireIdle(); return new(this); } }
    public override ConstantCurveAsset CreateCopy(Guid id = default) => (ConstantCurveAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());
    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using ConstantCurveAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<ConstantCurveAsset> OpenAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, pages, cancellationToken: cancellationToken).ConfigureAwait(false);
        AssetEntry entry = file.Describe();
        var asset = new ConstantCurveAsset(file.Pages, entry.AssetGuid);
        try
        {
            asset.ReadBlockContent(file);
            asset.AttachFile(file);
            await asset.GetBlockAsync<ConstantCurveBlock>(cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
    public ValueTask<AssetEntry> SaveSourceAsync(string path, CancellationToken cancellationToken = default)
        => AssetText.SaveAsync(path, this, static (writer, asset) => writer.WriteBlock(asset.GetBlock<ConstantCurveBlock>()), cancellationToken);
    public static async ValueTask<ConstantCurveAsset> OpenSourceAsync(string path, PageArena? pages = null, CancellationToken cancellationToken = default)
    {
        using AssetText.SourceReader source = await AssetText.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        source.RequireAsset<ConstantCurveAsset>(FileType);
        var asset = new ConstantCurveAsset(pages ?? PageArena.Shared, source.Description.AssetGuid) { Name = source.Description.Name };
        try { await source.ReadBlockIntoAsync(asset.GetBlock<ConstantCurveBlock>(), cancellationToken).ConfigureAwait(false); return asset; }
        catch { asset.Dispose(); throw; }
    }
}
