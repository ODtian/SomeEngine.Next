using System.Globalization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Texture
{
    internal static async ValueTask<Texture> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using BinaryDocument<Texture> document =
            await AssetProject.OpenAsync<Texture>(path, cancellationToken)
                .ConfigureAwait(false);
        return await MaterializeAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the deterministic semantic key for a two-dimensional mip/tile coordinate.</summary>
    internal static ulong MipTileChunkKey(uint mipLevel, uint tileX, uint tileY)
        => MipTileChunkKey(mipLevel, arrayLayer: 0, face: 0, depthSlice: 0, tileX, tileY);

    /// <summary>Returns the deterministic key for a complete array/cube/depth mip/tile coordinate.</summary>
    internal static ulong MipTileChunkKey(
        uint mipLevel,
        uint arrayLayer,
        uint face,
        uint depthSlice,
        uint tileX,
        uint tileY)
        => BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Texture.Mips." +
            mipLevel.ToString(CultureInfo.InvariantCulture) +
            ".Layers." + arrayLayer.ToString(CultureInfo.InvariantCulture) +
            ".Faces." + face.ToString(CultureInfo.InvariantCulture) +
            ".DepthSlices." + depthSlice.ToString(CultureInfo.InvariantCulture) +
            ".Tiles." + tileX.ToString(CultureInfo.InvariantCulture) +
            "." + tileY.ToString(CultureInfo.InvariantCulture));

    /// <summary>Validates root-only mip/tile metadata before a runtime scheduler is published.</summary>
    internal static void ValidateRoot(Texture root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateRootMetadata(root);
    }

    internal static BinaryDocumentWriter CreateWriter(Texture asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        IList<TextureMipTile> mipTiles = CreateCanonicalMipTiles(asset);

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(asset);
        for (int ordinal = 0; ordinal < mipTiles.Count; ordinal++)
        {
            TextureMipTile tile = mipTiles[ordinal];
            builder.AddChunk(
                tile.PayloadChunk.Key,
                tile.Payload!.Value,
                AssetMetadata.RawBytesTypeFingerprint,
                ChunkCompression.None,
                alignment: 4096,
                ordinal: checked((uint)ordinal));
        }

        return builder;
    }

    private static async ValueTask<Texture> MaterializeAsync(
        BinaryDocument<Texture> document,
        CancellationToken cancellationToken)
    {
        ValidateRootMetadata(document.Root);
        Texture result = document.Root;
        IList<TextureMipTile> mipTiles = result.MipTiles!;
        for (int index = 0; index < mipTiles.Count; index++)
        {
            TextureMipTile tile = mipTiles[index];
            Memory<byte>? payload = await document.TryReadChunkAsync(
                tile.PayloadChunk,
                static length => GC.AllocateUninitializedArray<byte>(length),
                cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue || checked((ulong)payload.Value.Length) != tile.DecodedLength)
                throw new InvalidDataException($"Texture {Describe(tile)} payload length disagrees with root metadata.");
            tile.Payload = payload;
        }

        return result;
    }

    private static IList<TextureMipTile> CreateCanonicalMipTiles(Texture root)
    {
        ValidateDescription(root);
        if (root.MipTiles is null || root.MipTiles.Count == 0)
            throw new InvalidDataException("Texture assets must contain mip/tile payloads.");

        IList<TextureMipTile> tiles = root.MipTiles;

        TextureMipTile? mipZero = tiles.FirstOrDefault(static tile =>
            tile.MipLevel == 0
            && tile.ArrayLayer == 0
            && tile.Face == 0
            && tile.DepthSlice == 0
            && tile.TileX == 0
            && tile.TileY == 0);
        if (mipZero is null)
            throw new InvalidDataException("Texture assets must contain mip 0 tile (0,0).");

        SortCanonicalInPlace(tiles);

        var semantics = new HashSet<(uint Mip, uint Layer, uint Face, uint Depth, uint X, uint Y)>();
        var keys = new HashSet<ulong>();
        foreach (TextureMipTile tile in tiles)
        {
            if (!semantics.Add((
                    tile.MipLevel,
                    tile.ArrayLayer,
                    tile.Face,
                    tile.DepthSlice,
                    tile.TileX,
                    tile.TileY)))
            {
                throw new InvalidDataException(
                    $"Texture contains duplicate {Describe(tile)}.");
            }

            ValidateTileBounds(root, tile);
            if (!tile.Payload.HasValue || tile.Payload.Value.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Texture {Describe(tile)} has no payload.");
            }

            tile.ChunkKey = MipTileChunkKey(
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileX,
                tile.TileY);
            tile.DecodedLength = checked((ulong)tile.Payload.Value.Length);
            ValidateStorageLayout(tile, tile.DecodedLength);
            if (!keys.Add(tile.ChunkKey))
                throw new InvalidDataException("Texture mip/tile semantic chunk keys collided.");
        }

        return tiles;
    }

    private static void SortCanonicalInPlace(IList<TextureMipTile> tiles)
    {
        for (int start = (tiles.Count / 2) - 1; start >= 0; start--)
            SiftDown(tiles, start, tiles.Count);
        for (int end = tiles.Count - 1; end > 0; end--)
        {
            (tiles[0], tiles[end]) = (tiles[end], tiles[0]);
            SiftDown(tiles, 0, end);
        }
    }

    private static void SiftDown(IList<TextureMipTile> tiles, int root, int count)
    {
        while (true)
        {
            int child = checked((root * 2) + 1);
            if (child >= count)
                return;
            if (child + 1 < count && CompareCanonical(tiles[child], tiles[child + 1]) < 0)
                child++;
            if (CompareCanonical(tiles[root], tiles[child]) >= 0)
                return;
            (tiles[root], tiles[child]) = (tiles[child], tiles[root]);
            root = child;
        }
    }

    private static int CompareCanonical(TextureMipTile left, TextureMipTile right)
    {
        int mip = left.MipLevel.CompareTo(right.MipLevel);
        if (mip != 0)
            return mip;
        int layer = left.ArrayLayer.CompareTo(right.ArrayLayer);
        if (layer != 0)
            return layer;
        int face = left.Face.CompareTo(right.Face);
        if (face != 0)
            return face;
        int depth = left.DepthSlice.CompareTo(right.DepthSlice);
        if (depth != 0)
            return depth;
        int y = left.TileY.CompareTo(right.TileY);
        return y != 0 ? y : left.TileX.CompareTo(right.TileX);
    }

    private static void ValidateRootMetadata(Texture root)
    {
        ValidateDescription(root);
        if (root.MipTiles is null || root.MipTiles.Count == 0)
            throw new InvalidDataException("Binary texture roots must declare mip/tile metadata.");

        bool hasMipZero = false;
        (uint Mip, uint Layer, uint Face, uint Depth, uint Y, uint X) previous = default;
        for (int index = 0; index < root.MipTiles.Count; index++)
        {
            TextureMipTile tile = root.MipTiles[index];
            if (tile.Payload.HasValue)
                throw new InvalidDataException("Binary texture mip/tile metadata must not inline payload bytes.");
            var current = (
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileY,
                tile.TileX);
            if (index > 0)
            {
                int order = previous.CompareTo(current);
                if (order == 0)
                    throw new InvalidDataException("Texture root contains duplicate mip/tile metadata.");
                if (order > 0)
                    throw new InvalidDataException("Texture mip/tile metadata is not in canonical order.");
            }
            previous = current;

            ValidateTileBounds(root, tile);
            if (tile.DecodedLength == 0)
                throw new InvalidDataException("Binary texture mip/tile metadata declares an empty payload.");
            ValidateStorageLayout(tile, tile.DecodedLength);
            ulong expectedKey = MipTileChunkKey(
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileX,
                tile.TileY);
            if (tile.ChunkKey != expectedKey)
                throw new InvalidDataException("Binary texture mip/tile metadata has a non-canonical chunk key.");
            hasMipZero |= tile.MipLevel == 0
                && tile.ArrayLayer == 0
                && tile.Face == 0
                && tile.DepthSlice == 0
                && tile.TileX == 0
                && tile.TileY == 0;
        }

        if (!hasMipZero)
            throw new InvalidDataException("Binary texture root is missing mip 0 tile (0,0).");
    }

    private static void ValidateTileBounds(Texture root, TextureMipTile tile)
    {
        bool cube = root.SampledDimension is
            SomeEngine.Graphics.TextureViewDimension.Cube or
            SomeEngine.Graphics.TextureViewDimension.CubeArray;
        if (tile.Face > 5)
            throw new InvalidDataException($"Texture {Describe(tile)} declares cube face {tile.Face}; maximum is 5.");
        if (!cube && tile.Face != 0)
            throw new InvalidDataException($"Texture {Describe(tile)} declares a cube face for a non-cube texture.");
        if (tile.MipLevel >= root.MipLevelCount)
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared mip count.");
        if (tile.ArrayLayer >= root.ArrayLayerCount)
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared array-layer count.");
        if (tile.DepthSlice >= MipExtent(root.Depth, tile.MipLevel))
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared depth extent.");
        uint mipWidth = MipExtent(root.Width, tile.MipLevel);
        uint mipHeight = MipExtent(root.Height, tile.MipLevel);
        if (root.Width != 0 && (tile.Width == 0 || tile.Width > mipWidth || tile.TileX >= mipWidth))
        {
            throw new InvalidDataException(
                $"Texture {Describe(tile)} width is outside " +
                $"the {mipWidth}-pixel mip extent.");
        }
        if (root.Height != 0 && (tile.Height == 0 || tile.Height > mipHeight || tile.TileY >= mipHeight))
        {
            throw new InvalidDataException(
                $"Texture {Describe(tile)} height is outside " +
                $"the {mipHeight}-pixel mip extent.");
        }
    }

    private static void ValidateDescription(Texture root)
    {
        if (root.Width == 0 || root.Height == 0 || root.Depth == 0)
            throw new InvalidDataException("Texture extents must be non-zero.");
        if (root.MipLevelCount == 0 || root.ArrayLayerCount == 0)
            throw new InvalidDataException("Texture mip and array-layer counts must be non-zero.");
        if (!Enum.IsDefined(root.Dimension) || !Enum.IsDefined(root.Format) ||
            !Enum.IsDefined(root.SampledFormat) || !Enum.IsDefined(root.SampledDimension))
        {
            throw new InvalidDataException("Texture resource or sampled-view metadata is invalid.");
        }

        bool validShape = root.Dimension switch
        {
            SomeEngine.Graphics.TextureDimension.Texture1D =>
                root.Height == 1 && root.Depth == 1 && root.SampledDimension is
                    SomeEngine.Graphics.TextureViewDimension.Texture1D or
                    SomeEngine.Graphics.TextureViewDimension.Texture1DArray,
            SomeEngine.Graphics.TextureDimension.Texture2D =>
                root.Depth == 1 && root.SampledDimension is
                    SomeEngine.Graphics.TextureViewDimension.Texture2D or
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray or
                    SomeEngine.Graphics.TextureViewDimension.Cube or
                    SomeEngine.Graphics.TextureViewDimension.CubeArray,
            SomeEngine.Graphics.TextureDimension.Texture3D =>
                root.ArrayLayerCount == 1 &&
                root.SampledDimension == SomeEngine.Graphics.TextureViewDimension.Texture3D,
            _ => false,
        };
        if (!validShape)
            throw new InvalidDataException("Texture resource and sampled-view dimensions are incompatible.");
        if (root.SampledDimension is
                SomeEngine.Graphics.TextureViewDimension.Cube or
                SomeEngine.Graphics.TextureViewDimension.CubeArray
            && root.Width != root.Height)
        {
            throw new InvalidDataException("Cube textures must have equal width and height.");
        }
    }

    private static uint MipExtent(uint baseExtent, uint mipLevel)
        => baseExtent == 0
            ? 0
            : Math.Max(1u, baseExtent >> checked((int)Math.Min(mipLevel, 31u)));

    internal static TextureMipTile RequireMipTile(
        Texture root,
        uint mipLevel,
        uint arrayLayer,
        uint face,
        uint depthSlice,
        uint tileX,
        uint tileY)
    {
        IList<TextureMipTile>? mipTiles = root.MipTiles;
        if (mipTiles is not null)
        {
            foreach (TextureMipTile tile in mipTiles)
            {
                if (tile.MipLevel == mipLevel
                    && tile.ArrayLayer == arrayLayer
                    && tile.Face == face
                    && tile.DepthSlice == depthSlice
                    && tile.TileX == tileX
                    && tile.TileY == tileY)
                    return tile;
            }
        }

        throw new KeyNotFoundException(
            $"Texture mip {mipLevel}, layer {arrayLayer}, face {face}, depth slice {depthSlice}, " +
            $"tile ({tileX},{tileY}) is not declared by the root metadata.");
    }

    private static void ValidateStorageLayout(TextureMipTile tile, ulong decodedLength)
    {
        if (tile.RowPitch > decodedLength)
            throw new InvalidDataException($"Texture {Describe(tile)} row pitch exceeds its payload length.");
        if (tile.SlicePitch != 0
            && (tile.RowPitch == 0 || tile.SlicePitch < tile.RowPitch || tile.SlicePitch > decodedLength))
        {
            throw new InvalidDataException(
                $"Texture {Describe(tile)} slice pitch must be between its row pitch and payload length.");
        }
    }

    private static string Describe(TextureMipTile tile)
        => $"mip {tile.MipLevel}, layer {tile.ArrayLayer}, face {tile.Face}, depth slice " +
           $"{tile.DepthSlice}, tile ({tile.TileX},{tile.TileY})";
}
