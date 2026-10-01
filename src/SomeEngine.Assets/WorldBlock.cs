using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>ECS topology and components encoded by the caller's existing ECS codecs.</summary>
[AssetBlock("SomeEngine.Assets.WorldBlock"), BinarySerializable]
[BinaryFields(nameof(ContentLength), nameof(ExternalReferences))]
public sealed partial class WorldBlock : AssetBlock
{
    public static ulong ContentKey { get; } = BinaryFieldKey.FromName("SomeEngine.ECS.World.Content");
    public partial ulong ContentLength { get; set; }
    public partial ImmutableArray<AssetId<Asset>> ExternalReferences { get; set; }
    partial void Initialize() => ExternalReferences = [];
    /// <summary>Encodes an explicit World edit into an immutable array, without saving or changing the supplied World.</summary>
    public void WriteWorld(World world, SerializationRegistry registry, SerializeOptions options = default)
    {
        lock (SyncRoot) RequireIdle();
        var references = new HashSet<ExternalReferenceKey>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, registry, options with { ExternalReferences = references });
        ImmutableArray<AssetId<Asset>> ids = references.Select(static reference => new AssetId<Asset>(reference.Value))
            .Distinct().OrderBy(static id => id.Value).ToImmutableArray();
        byte[] content = stream.ToArray();
        lock (SyncRoot)
        {
            RequireIdle();
            SetBytes(ContentKey, content);
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
        ReadOnlyMemory<byte> content = await copy.ReadBytesAsync(ContentKey, cancellationToken).ConfigureAwait(false);
        if ((ulong)content.Length != copy.ContentLength)
            throw new InvalidDataException("The World chunk length differs from its asset declaration.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(content, out ArraySegment<byte> data))
            data = new(content.ToArray());
        using var stream = new MemoryStream(data.Array!, data.Offset, data.Count, writable: false);
        return WorldSerializer.ReadWorld(stream, registry, options with { RequiredContract = SerializationContract.DurableSave });
    }

    protected override void ValidateContent()
    {
        if (ContentLength == 0) throw new InvalidOperationException("Write a World before saving its block.");
        if (Chunks.ContainsKey(ContentKey) && (ulong)Chunks[ContentKey].Length != ContentLength)
            throw new InvalidDataException("World content differs from its declared length.");
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        if (Chunks.ContainsKey(ContentKey)) writer.WriteChunk(ContentKey, Chunks[ContentKey], alignment: 4096);
        else
        {
            using AssetBlockSource source = RetainSource() ?? throw new InvalidDataException("World block has no content source.");
            await writer.CopyChunkAsync(source, ContentKey, cancellationToken).ConfigureAwait(false);
        }
    }
}
