using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.NavigationLinkBlock")]
public sealed partial class NavigationLinkBlock : AssetBlock, IAssetBlockWriter
{
    public const ulong LinksChunkKey = 1;
    private NavigationLinkSet _links = new(Array.Empty<NavigationLink>());

    public NavigationLinkBlock() { }
    private NavigationLinkBlock(NavigationLinkBlock source, object? syncRoot = null) : base(source, syncRoot) => _links = source._links;
    public override NavigationLinkBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationLinkBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public void SetLinks(NavigationLinkSet links)
    {
        lock (SyncRoot) { RequireIdle(); _links = links; MarkChanged(); }
    }

    public NavigationLinkSet Create()
    {
        lock (SyncRoot) { RequireIdle(); return _links; }
    }

    public ValueTask<NavigationLinkSet> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create());
    }

    public static async ValueTask<NavigationLinkBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationLinkData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> bytes = await source.ReadBytesAsync(data.LinksChunk, cancellationToken).ConfigureAwait(false);
        var result = new NavigationLinkBlock { _links = new(NavigationArrayData.Decode<NavigationLink>(bytes)) };
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigationLinkSet links = Create();
        writer.WriteMetadata(BlockTypeId, new NavigationLinkData { LinksChunk = LinksChunkKey });
        writer.WriteChunk(LinksChunkKey, NavigationArrayData.Encode(links.Links));
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _links = null!;
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class NavigationLinkData
{
    public ulong LinksChunk { get; set; }
}
