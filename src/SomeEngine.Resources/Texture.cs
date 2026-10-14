using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;
using GraphicsViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Resources;

/// <summary>Immutable managed pixels for one texture tile. The caller must not modify published bytes.</summary>
public readonly record struct TexturePixels(TextureMipTile Tile, ReadOnlyMemory<byte> Bytes);

/// <summary>Texture shape and ready immutable pixels, independent of asset files.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed partial class Texture : IDisposable, IAsyncDisposable
{
    private ReadOnlyMemory<byte>[] _pixels = [];
    public Guid DataVersion { get; internal set; }
    private int _disposed;
    private Texture() { }

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
    public ulong PixelsRevision { get; private set; } = 1;
    public TextureVersions Versions
    {
        get { lock (_deviceGate) return new(Revision, PixelsRevision, _replacement?.Revision ?? Revision); }
    }
    public uint BootstrapMipLevel => MipLevelCount - 1;
    public bool HasBootstrap
    {
        get
        {
            lock (_deviceGate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return MipTiles.Select((tile, index) => (tile, index)).Where(row => row.tile.MipLevel == BootstrapMipLevel).All(row => !_pixels[row.index].IsEmpty);
            }
        }
    }

    public TextureDesc GetDescription() => new(Dimension, Width, Height, Depth, MipLevelCount,
        TextureTiles.IsCube(SampledDimension) ? checked(ArrayLayerCount * 6) : ArrayLayerCount,
        1, Format, TextureUsages.Sampled | TextureUsages.CopyDestination, label: Name);

    public static Texture Create(in TextureDesc description, IReadOnlyList<TexturePixels> pixels,
        GraphicsViewDimension sampledDimension, Format? sampledFormat = null)
    {
        TexturePixels[] values = pixels.Select(input => new TexturePixels(TextureTiles.Describe(input.Tile, input.Bytes.Length), input.Bytes)).ToArray();
        TextureMipTile[] tiles = values.Select(input => input.Tile).ToArray();
        Array.Sort(tiles, TextureTiles.Compare);
        return Create(description, tiles, values, sampledDimension, sampledFormat);
    }

    public static Texture Create(in TextureDesc description, IReadOnlyList<TextureMipTile> tiles,
        IReadOnlyList<TexturePixels> pixels, GraphicsViewDimension sampledDimension, Format? sampledFormat = null)
    {
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
            MipTiles = tiles.ToImmutableList(), _pixels = new ReadOnlyMemory<byte>[tiles.Count],
        };
        foreach (TexturePixels input in pixels)
        {
            int index = result.FindTile(input.Tile.ChunkKey);
            result.ValidatePixels(index, input.Bytes);
            if (!result._pixels[index].IsEmpty) throw new InvalidDataException("Texture pixels repeat a tile.");
            result._pixels[index] = input.Bytes;
        }
        foreach (TextureMipTile tile in bootstrap)
            if (result._pixels[result.FindTile(tile.ChunkKey)].IsEmpty)
                throw new InvalidDataException("Texture pixels do not contain the complete coarsest mip.");
        return result;
    }

    public bool TryGetTile(ulong key, out ReadOnlyMemory<byte> pixels)
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            int index = MipTiles.FindIndex(tile => tile.ChunkKey == key);
            pixels = index < 0 ? default : _pixels[index];
            return !pixels.IsEmpty;
        }
    }

    public void AddPixels(IReadOnlyList<TexturePixels> pixels, ulong expectedRevision)
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (Revision != expectedRevision || _updateCompletion is not null)
                throw new InvalidOperationException("The texture changed while its additional pixels were being acquired.");
            var replacement = (ReadOnlyMemory<byte>[])_pixels.Clone();
            bool changed = false;
            foreach (TexturePixels input in pixels)
            {
                int index = FindTile(input.Tile.ChunkKey);
                ValidatePixels(index, input.Bytes);
                if (replacement[index].IsEmpty)
                {
                    replacement[index] = input.Bytes;
                    changed = true;
                }
            }
            if (changed)
            {
                _pixels = replacement;
                PixelsRevision++;
            }
        }
    }

    private int FindTile(ulong key)
    {
        int index = MipTiles.FindIndex(tile => tile.ChunkKey == key);
        return index >= 0 ? index : throw new InvalidDataException("Pixels contain an unknown tile.");
    }
    private void ValidatePixels(int index, ReadOnlyMemory<byte> pixels)
    {
        if ((ulong)pixels.Length != MipTiles[index].DecodedLength)
            throw new InvalidDataException("Texture pixels differ from the declared tile length.");
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
        return UpdateAsync(Create(description, pixels, sampledDimension, sampledFormat), cancellationToken);
    }

    private static ValueTask ApplyReloadAsync(Texture current, Texture replacement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong revision = checked(current.Revision + 1);
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
        current._pixels = replacement._pixels;
        current.DataVersion = replacement.DataVersion;
        current.Revision = revision;
        current.PixelsRevision++;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { ReleaseDevices(); }
        finally
        {
            lock (_deviceGate) _pixels = [];
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
