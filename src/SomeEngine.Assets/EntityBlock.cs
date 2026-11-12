using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>Selected entities encoded with their component values and topology.</summary>
[AssetBlock("SomeEngine.Assets.EntityBlock"), BinarySerializable]
[BinaryFields(nameof(ContentLength), nameof(ExternalReferences))]
public sealed partial class EntityBlock : AssetBlock
{
    public static ulong ContentKey { get; } = BinaryFieldKey.FromName("SomeEngine.ECS.Entities.Content");
    public partial ulong ContentLength { get; set; }
    public partial ImmutableArray<AssetId<Asset>> ExternalReferences { get; set; }

    partial void Initialize() => ExternalReferences = [];

    public void WriteEntities(World world, ReadOnlySpan<Entity> entities, SerializeOptions options = default)
    {
        lock (SyncRoot) RequireIdle();
        var references = new HashSet<ExternalReferenceKey>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteEntities(stream, world, entities,
            options with { Purpose = SavePurpose.DurableSave, ExternalReferences = references });
        ImmutableArray<AssetId<Asset>> ids = references
            .Select(static reference => new AssetId<Asset>(reference.Value))
            .OrderBy(static id => id.Value).ToImmutableArray();
        byte[] content = stream.ToArray();
        lock (SyncRoot)
        {
            RequireIdle();
            SetBytes(ContentKey, content);
            ContentLength = (ulong)content.Length;
            ExternalReferences = ids;
        }
        if (options.ExternalReferences is { } output)
            foreach (ExternalReferenceKey reference in references) output.Add(reference);
    }

    protected override void ValidateContent()
    {
        if (ContentLength == 0) throw new InvalidOperationException("Write entities before saving their block.");
        if (Chunks.TryGetValue(ContentKey, out ReadOnlyMemory<byte> content) && (ulong)content.Length != ContentLength)
            throw new InvalidDataException("Entity content differs from its declared length.");
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        if (Chunks.TryGetValue(ContentKey, out ReadOnlyMemory<byte> content))
            writer.WriteChunk(ContentKey, content, alignment: 4096);
        else
        {
            using AssetBlockSource source = RetainSource()
                ?? throw new InvalidDataException("Entity block requires its encoded content source.");
            await writer.CopyChunkAsync(source, ContentKey, cancellationToken).ConfigureAwait(false);
        }
    }
}
