using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;
using GraphicsViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Resources;

/// <summary>Caller-provided tile pixels; Texture.Create copies them into its retained pages.</summary>
public readonly record struct TexturePixels(TextureMipTile Tile, ReadOnlyMemory<byte> Bytes);

/// <summary>Texture shape and ready pixel pages, independent of asset files and reading schedulers.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed partial class Texture : IDisposable, IAsyncDisposable
{
    private PageBufferMap? _pixels;
    private int _disposed;
    private Texture() { }
    private static long _nextTextureId;
    private long _textureId = Interlocked.Increment(ref _nextTextureId);
    private readonly List<Texture> _capturedContents = [];
    private Texture? _capturedContent;
    public long TextureId => _textureId;
    public event Action<Texture>? Changed;

    /// <summary>Retains this version's ready pixels for extraction into the rendering world.</summary>
    public Texture CaptureData()
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_capturedContent is not null && _capturedContent.Revision == Revision) return _capturedContent;
            Texture content = Create(GetDescription(), MipTiles, _pixels!, SampledDimension, SampledFormat);
            content.Revision = Revision;
            content._textureId = _textureId;
            _capturedContents.Add(content);
            return _capturedContent = content;
        }
    }

    public string? Name { get; private set; }
    public TextureDimension Dimension { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint Depth { get; private set; }
    public uint MipLevelCount { get; private set; }
    public uint ArrayLayerCount { get; private set; }
    public Format Format { get; private set; }
    public Format SampledFormat { get; private set; }
    public GraphicsViewDimension SampledDimension { get; private set; }
    public ImmutableList<TextureMipTile> MipTiles { get; private set; } = [];
    public ulong Revision { get; private set; } = 1;
    public uint BootstrapMipLevel => MipLevelCount - 1;
    public PageArena Pages => (_pixels ?? throw new ObjectDisposedException(nameof(Texture))).Arena;
    public bool HasBootstrap
    {
        get
        {
            lock (_deviceGate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return MipTiles.Where(tile => tile.MipLevel == BootstrapMipLevel).All(tile => _pixels!.Contains(tile.ChunkKey));
            }
        }
    }

    public TextureDesc GetDescription() => new(Dimension, Width, Height, Depth, MipLevelCount,
        TextureTiles.IsCube(SampledDimension) ? checked(ArrayLayerCount * 6) : ArrayLayerCount,
        1, Format, TextureUsages.Sampled | TextureUsages.CopyDestination, label: Name);

    public static Texture Create(in TextureDesc description, IReadOnlyList<TexturePixels> pixels,
        GraphicsViewDimension sampledDimension, Format? sampledFormat = null, PageArena? pages = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        using var content = new PageBufferMap(pages ?? PageArena.Shared);
        var tiles = new TextureMipTile[pixels.Count];
        for (int index = 0; index < pixels.Count; index++)
        {
            TexturePixels input = pixels[index];
            TextureMipTile tile = TextureTiles.Describe(input.Tile, input.Bytes.Length);
            if (content.Contains(tile.ChunkKey)) throw new InvalidDataException("Texture pixels repeat a tile.");
            content.Set(tile.ChunkKey, input.Bytes.Span);
            tiles[index] = tile;
        }
        Array.Sort(tiles, TextureTiles.Compare);
        return Create(description, tiles, content, sampledDimension, sampledFormat);
    }

    public static Texture Create(in TextureDesc description, IReadOnlyList<TextureMipTile> tiles, PageBufferMap pixels,
        GraphicsViewDimension sampledDimension, Format? sampledFormat = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Format viewFormat = sampledFormat ?? description.Format;
        TextureTiles.Validate(description, sampledDimension, viewFormat, tiles);
        TextureMipTile[] bootstrap = TextureTiles.Bootstrap(description, sampledDimension, tiles);
        var result = new Texture
        {
            Name = description.Label, Dimension = description.Dimension,
            Width = description.Width, Height = description.Height, Depth = description.Depth,
            MipLevelCount = description.MipLevelCount,
            ArrayLayerCount = TextureTiles.IsCube(sampledDimension) ? description.ArrayLayerCount / 6 : description.ArrayLayerCount,
            Format = description.Format, SampledFormat = viewFormat, SampledDimension = sampledDimension,
            MipTiles = tiles.ToImmutableList(),
            _pixels = pixels.Duplicate(),
        };
        try
        {
            result.ValidatePixels(result._pixels);
            foreach (TextureMipTile tile in bootstrap)
                if (!result._pixels.Contains(tile.ChunkKey)) throw new InvalidDataException("Texture pixels do not contain the complete coarsest mip.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public bool TryRetainTile(ulong key, [NotNullWhen(true)] out PageRange<byte>? pixels)
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            pixels = _pixels!.Contains(key) ? _pixels.Retain(key) : null;
            return pixels is not null;
        }
    }

    /// <summary>Adds explicitly acquired tiles to this content revision. Existing ready tiles are unchanged.</summary>
    public void AddPixels(PageBufferMap pixels, ulong expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        using PageBufferMap input = pixels.Duplicate();
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (Revision != expectedRevision || _updateCompletion is not null)
                throw new InvalidOperationException("The texture changed while its additional pixels were being acquired.");
            ValidatePixels(input);
            PageBufferMap replacement = _pixels!.Duplicate();
            try
            {
                foreach (ulong key in input.Keys)
                {
                    if (replacement.Contains(key)) continue;
                    using PageBuffer bytes = input.DuplicateBuffer(key);
                    replacement.Set(key, bytes);
                }
            }
            catch { replacement.Dispose(); throw; }
            PageBufferMap previous = _pixels;
            _pixels = replacement;
            previous.Dispose();
        }
    }

    private void ValidatePixels(PageBufferMap pixels)
    {
        foreach (ulong key in pixels.Keys)
        {
            TextureMipTile tile = MipTiles.FirstOrDefault(tile => tile.ChunkKey == key)
                ?? throw new InvalidDataException("Pixels contain a tile absent from the texture description.");
            if ((ulong)pixels.GetLength(key) != tile.DecodedLength)
                throw new InvalidDataException("Texture pixels differ from the declared tile length.");
        }
    }

    /// <summary>Transfers ready replacement content through the existing device completion boundary.</summary>
    public async ValueTask UpdateAsync(Texture replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ObjectDisposedException.ThrowIf(replacement.IsDisposed, replacement);
        if (ReferenceEquals(this, replacement)) throw new ArgumentException("A texture cannot replace itself.", nameof(replacement));
        await using (replacement)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task completion = QueueUpdate(replacement);
            using CancellationTokenRegistration cancel = cancellationToken.Register(() =>
                FailDeviceUpdate(replacement, new OperationCanceledException(cancellationToken)));
            await completion.ConfigureAwait(false);
        }
    }

    public ValueTask UpdateAsync(in TextureDesc description, IReadOnlyList<TexturePixels> pixels,
        GraphicsViewDimension sampledDimension, Format? sampledFormat = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return UpdateAsync(Create(description, pixels, sampledDimension, sampledFormat, Pages), cancellationToken);
    }

    private static ValueTask ApplyReloadAsync(Texture current, Texture replacement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong revision = checked(current.Revision + 1);
        PageBufferMap? previous = current._pixels;
        current.Name = replacement.Name;
        current.Dimension = replacement.Dimension;
        current.Width = replacement.Width;
        current.Height = replacement.Height;
        current.Depth = replacement.Depth;
        current.MipLevelCount = replacement.MipLevelCount;
        current.ArrayLayerCount = replacement.ArrayLayerCount;
        current.Format = replacement.Format;
        current.SampledFormat = replacement.SampledFormat;
        current.SampledDimension = replacement.SampledDimension;
        current.MipTiles = replacement.MipTiles;
        current._pixels = Interlocked.Exchange(ref replacement._pixels, null);
        current.Revision = revision;
        previous?.Dispose();
        current.Changed?.Invoke(current);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { ReleaseDevices(); }
        finally
        {
            lock (_deviceGate)
            {
                Interlocked.Exchange(ref _pixels, null)?.Dispose();
                foreach (Texture content in _capturedContents) content.Dispose();
                _capturedContents.Clear();
                _capturedContent = null;
            }
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
