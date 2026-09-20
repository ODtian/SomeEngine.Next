using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>ECS topology and components encoded by the caller's existing ECS codecs.</summary>
[AssetBlock("SomeEngine.Assets.WorldBlock"), BinarySerializable]
[BinaryFields(nameof(ContentLength), nameof(ExternalReferences))]
public sealed partial class WorldBlock : AssetPageBlock
{
    public static ulong ContentKey { get; } = BinaryFieldKey.FromName("SomeEngine.ECS.World.Content");
    public partial ulong ContentLength { get; set; }
    public partial ImmutableArray<AssetId<Asset>> ExternalReferences { get; set; }
    partial void Initialize() => ExternalReferences = [];
    /// <summary>Encodes an explicit World edit into pages, without saving or changing the supplied World.</summary>
    public void WriteWorld(World world, SerializationRegistry registry, SerializeOptions options = default)
    {
        lock (Access.SyncRoot) RequireIdle();
        var references = new HashSet<ExternalReferenceKey>();
        using var bytes = new PageBuffer(Pages);
        using var stream = new PageBufferStream(bytes);
        WorldSerializer.WriteDurableWorld(stream, world, registry, options with { ExternalReferences = references });
        ImmutableArray<AssetId<Asset>> ids = references.Select(static reference => new AssetId<Asset>(reference.Value))
            .Distinct().OrderBy(static id => id.Value).ToImmutableArray();
        using PageBuffer content = bytes.Slice(0, stream.Length);
        lock (Access.SyncRoot)
        {
            RequireIdle();
            Chunks.Set(ContentKey, content);
            ContentLength = checked((ulong)stream.Length);
            ExternalReferences = ids;
        }
        if (options.ExternalReferences is { } output)
            foreach (ExternalReferenceKey reference in references) output.Add(reference);
    }

    /// <summary>Completes all reads before returning an independent World.</summary>
    public async ValueTask<World> LoadAsync(SerializationRegistry registry, WorldLoadOptions options = default,
        CancellationToken cancellationToken = default)
    {
        using WorldBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        using PageBuffer content = await copy.AcquireBufferAsync(ContentKey, cancellationToken).ConfigureAwait(false);
        if ((ulong)content.Length != copy.ContentLength)
            throw new InvalidDataException("The World chunk length differs from its asset declaration.");
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new PageBufferStream(content, writable: false);
        return WorldSerializer.ReadWorld(stream, registry, options with { RequiredContract = SerializationContract.DurableSave });
    }

    protected override void ValidateContent()
    {
        if (ContentLength == 0) throw new InvalidOperationException("Write a World before saving its block.");
        if (Chunks.Contains(ContentKey) && (ulong)Chunks.GetLength(ContentKey) != ContentLength)
            throw new InvalidDataException("World content differs from its declared length.");
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        if (Chunks.Contains(ContentKey)) writer.WriteChunk(ContentKey, Chunks, alignment: 4096);
        else
        {
            using AssetBlockSource source = RetainSource() ?? throw new InvalidDataException("World block has no content source.");
            await writer.CopyChunkAsync(source, ContentKey, cancellationToken).ConfigureAwait(false);
        }
    }
}
