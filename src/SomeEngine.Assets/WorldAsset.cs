using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>A World file uses the ECS component and topology codecs supplied by its caller.</summary>
public sealed class WorldAsset : Asset<World>
{
    public const string FileType = "SomeEngine.ECS.World";
    public const string PathSuffix = ".scene.asset";
    public static ulong SchemaHash => WorldFile.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => new(WorldFile.TypeId, SchemaHash, WorldFile.Compatibility, WorldFile.SchemaEpoch);
    private static readonly ulong ContentKey = BinaryFieldKey.FromName("SomeEngine.ECS.World.Content");
    private SharedBytes? _content;
    private ulong _contentLength;
    private Guid[] _references = [];

    public WorldAsset(AssetId<World> id = default) : base(id) { }
    private WorldAsset(WorldAsset source, AssetId<World> id) : base(source, id)
    {
        _content = source._content?.Retain();
        _contentLength = source._contentLength;
        _references = source._references;
    }
    public WorldAsset Duplicate(AssetId<World> id = default)
    {
        lock (Access) { RequireIdle(); return new(this, id); }
    }
    protected override void ReleaseFields() => _content?.Dispose();

    /// <summary>Encodes an explicit World edit; it does not write to disk or change the supplied World.</summary>
    public void WriteWorld(World world, SerializationRegistry registry, SerializeOptions options = default)
    {
        RequireOpen();
        var references = new HashSet<ExternalReferenceKey>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, registry, options with { ExternalReferences = references });
        if (!stream.TryGetBuffer(out ArraySegment<byte> bytes)) throw new InvalidOperationException("World output has no byte storage.");
        var content = new SharedBytes(bytes.AsMemory());
        lock (Access)
        {
            try { RequireIdle(); }
            catch { content.Dispose(); throw; }
            SharedBytes? previous = _content;
            _content = content;
            _contentLength = checked((ulong)bytes.Count);
            _references = references.Select(static reference => reference.Value).Order().ToArray();
            previous?.Dispose();
        }
        if (options.ExternalReferences is { } output)
            foreach (ExternalReferenceKey reference in references) output.Add(reference);
    }

    public async ValueTask<World> LoadAsync(SerializationRegistry registry, WorldLoadOptions options = default,
        CancellationToken cancellationToken = default)
    {
        using WorldAsset copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        await using IRangeSource source = copy._content is { } content
            ? new MemoryRangeSource(content.Read)
            : await (copy.Source ?? throw new InvalidOperationException("The asset has no World content."))
                .OpenChunkRangeSourceAsync(new BinaryChunkRef(ContentKey, checked((long)copy._contentLength)), cancellationToken).ConfigureAwait(false);
        using var stream = new RangeReadStream(source, cancellationToken);
        return WorldSerializer.ReadWorld(stream, registry, options with { RequiredContract = SerializationContract.DurableSave });
    }

    public AssetEntry Describe()
    {
        lock (Access) { RequireIdle(); return Describe(FileType, SchemaHash, _references); }
    }
    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using WorldAsset copy = Duplicate();
        if (copy._contentLength == 0) throw new InvalidOperationException("Write a World before saving this asset.");
        var values = new WorldFile { AssetGuid = copy.Id.Value, Name = copy.Name,
            ContentLength = copy._contentLength, References = copy._references };
        BinaryDocumentWriter writer = BinaryDocumentWriter.Create(values);
        if (copy._content is { } content) writer.AddChunk(ContentKey, content.Read, ByteSchema, alignment: 4096);
        else await writer.AddChunkAsync(ContentKey, copy.Source!, cancellationToken).ConfigureAwait(false);
        return await copy.SaveAsync(writer, path, PathSuffix, copy.Describe(), cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<WorldAsset> OpenAsync(AssetLoader loader, AssetId<World> id,
        CancellationToken cancellationToken = default)
    {
        var source = await loader.OpenSourceAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        await using BinaryDocument document = source.Document;
        return Read(document, source.Path, id);
    }
    public static async ValueTask<WorldAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var document = await OpenFileAsync(path, PathSuffix, WireType, cancellationToken).ConfigureAwait(false);
        return Read(document, Path.GetFullPath(path));
    }
    private static WorldAsset Read(BinaryDocument document, string path, AssetId<World> expected = default)
    {
        WorldFile values = document.ReadRoot<WorldFile>();
        if (values.AssetGuid == Guid.Empty || (!expected.IsEmpty && values.AssetGuid != expected.Value))
            throw new InvalidDataException("The World file has a different or empty asset ID.");
        if (values.ContentLength == 0) throw new InvalidDataException("The World file has no content.");
        var asset = new WorldAsset(new AssetId<World>(values.AssetGuid))
        {
            Name = values.Name, _contentLength = values.ContentLength, _references = values.References,
        };
        asset.AttachSource(document);
        asset.SetSourcePath(path);
        return asset;
    }
}

// Only file-level information lives here. Entity/component/topology data uses the ECS format.
[BinaryContract(BinaryCompatibility.ExactSchema)]
internal sealed partial class WorldFile
{
    public Guid AssetGuid { get; set; }
    public string Name { get; set; } = "";
    public ulong ContentLength { get; set; }
    public Guid[] References { get; set; } = [];
}
