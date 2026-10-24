using System.Globalization;
using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Resources;

public static class TextureTiles
{
    public static bool IsCube(SomeEngine.Graphics.TextureViewDimension dimension)
        => dimension is SomeEngine.Graphics.TextureViewDimension.Cube or SomeEngine.Graphics.TextureViewDimension.CubeArray;

    public static TextureMipTile Describe(TextureMipTile tile, long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        return tile with
        {
            ChunkKey = ChunkKey(tile.MipLevel, tile.ArrayLayer, tile.Face, tile.DepthSlice, tile.TileX, tile.TileY),
            DecodedLength = checked((ulong)byteLength),
        };
    }
    public static ulong ChunkKey(uint mipLevel, uint tileX, uint tileY)
        => ChunkKey(mipLevel, arrayLayer: 0, face: 0, depthSlice: 0, tileX, tileY);

    /// <summary>Returns the deterministic key for a complete array/cube/depth mip/tile coordinate.</summary>
    public static ulong ChunkKey(
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

    public static int Compare(TextureMipTile left, TextureMipTile right)
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

    public static void Validate(in TextureDesc description, SomeEngine.Graphics.TextureViewDimension sampledDimension, Format sampledFormat, IReadOnlyList<TextureMipTile> tiles)
    {
        ValidateDescription(description, sampledDimension, sampledFormat);
        if (tiles is null || tiles.Count == 0)
            throw new InvalidDataException("Binary texture roots must declare mip/tile metadata.");

        bool hasMipZero = false;
        (uint Mip, uint Layer, uint Face, uint Depth, uint Y, uint X) previous = default;
        for (int index = 0; index < tiles.Count; index++)
        {
            TextureMipTile tile = tiles[index];
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

            ValidateTileBounds(description, sampledDimension, tile);
            if (tile.DecodedLength == 0 || tile.DecodedLength > long.MaxValue)
                throw new InvalidDataException("Binary texture mip/tile metadata declares an invalid payload length.");
            ValidateStorageLayout(tile, tile.DecodedLength);
            ulong expectedKey = ChunkKey(
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

    private static void ValidateTileBounds(in TextureDesc description, SomeEngine.Graphics.TextureViewDimension sampledDimension, TextureMipTile tile)
    {
        bool cube = sampledDimension is
            SomeEngine.Graphics.TextureViewDimension.Cube or
            SomeEngine.Graphics.TextureViewDimension.CubeArray;
        if (tile.Face > 5)
            throw new InvalidDataException($"Texture {Describe(tile)} declares cube face {tile.Face}; maximum is 5.");
        if (!cube && tile.Face != 0)
            throw new InvalidDataException($"Texture {Describe(tile)} declares a cube face for a non-cube texture.");
        if (tile.MipLevel >= description.MipLevelCount)
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared mip count.");
        if (tile.ArrayLayer >= (IsCube(sampledDimension) ? description.ArrayLayerCount / 6 : description.ArrayLayerCount))
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared array-layer count.");
        if (tile.DepthSlice >= MipExtent(description.Depth, tile.MipLevel))
            throw new InvalidDataException($"Texture {Describe(tile)} exceeds the declared depth extent.");
        uint mipWidth = MipExtent(description.Width, tile.MipLevel);
        uint mipHeight = MipExtent(description.Height, tile.MipLevel);
        if (description.Width != 0 && (tile.Width == 0 || tile.TileX >= mipWidth ||
            tile.Width > mipWidth - tile.TileX))
        {
            throw new InvalidDataException(
                $"Texture {Describe(tile)} width is outside " +
                $"the {mipWidth}-pixel mip extent.");
        }
        if (description.Height != 0 && (tile.Height == 0 || tile.TileY >= mipHeight ||
            tile.Height > mipHeight - tile.TileY))
        {
            throw new InvalidDataException(
                $"Texture {Describe(tile)} height is outside " +
                $"the {mipHeight}-pixel mip extent.");
        }
    }

    private static void ValidateDescription(in TextureDesc description, SomeEngine.Graphics.TextureViewDimension sampledDimension, Format sampledFormat)
    {
        if (description.SampleCount != 1)
            throw new ArgumentException("Uploaded texture content requires one sample per texel.", nameof(description));
        if (IsCube(sampledDimension) && description.ArrayLayerCount % 6 != 0)
            throw new ArgumentException("Cube texture arrays require six resource layers per cube.", nameof(description));
        if (description.Width == 0 || description.Height == 0 || description.Depth == 0)
            throw new InvalidDataException("Texture extents must be non-zero.");
        if (description.MipLevelCount == 0 || (IsCube(sampledDimension) ? description.ArrayLayerCount / 6 : description.ArrayLayerCount) == 0)
            throw new InvalidDataException("Texture mip and array-layer counts must be non-zero.");
        if (!Enum.IsDefined(description.Dimension) || !Enum.IsDefined(description.Format) ||
            !Enum.IsDefined(sampledFormat) || !Enum.IsDefined(sampledDimension))
        {
            throw new InvalidDataException("Texture resource or sampled-view metadata is invalid.");
        }

        bool validShape = description.Dimension switch
        {
            SomeEngine.Graphics.TextureDimension.Texture1D =>
                description.Height == 1 && description.Depth == 1 && sampledDimension is
                    SomeEngine.Graphics.TextureViewDimension.Texture1D or
                    SomeEngine.Graphics.TextureViewDimension.Texture1DArray,
            SomeEngine.Graphics.TextureDimension.Texture2D =>
                description.Depth == 1 && sampledDimension is
                    SomeEngine.Graphics.TextureViewDimension.Texture2D or
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray or
                    SomeEngine.Graphics.TextureViewDimension.Cube or
                    SomeEngine.Graphics.TextureViewDimension.CubeArray,
            SomeEngine.Graphics.TextureDimension.Texture3D =>
                (IsCube(sampledDimension) ? description.ArrayLayerCount / 6 : description.ArrayLayerCount) == 1 &&
                sampledDimension == SomeEngine.Graphics.TextureViewDimension.Texture3D,
            _ => false,
        };
        if (!validShape)
            throw new InvalidDataException("Texture resource and sampled-view dimensions are incompatible.");
        if (sampledDimension is
                SomeEngine.Graphics.TextureViewDimension.Cube or
                SomeEngine.Graphics.TextureViewDimension.CubeArray
            && description.Width != description.Height)
        {
            throw new InvalidDataException("Cube textures must have equal width and height.");
        }
    }

    public static uint MipExtent(uint baseExtent, uint mipLevel)
        => baseExtent == 0
            ? 0
            : Math.Max(1u, baseExtent >> checked((int)Math.Min(mipLevel, 31u)));

    public static TextureMipTile Require(
        IReadOnlyList<TextureMipTile> tiles,
        uint mipLevel,
        uint arrayLayer,
        uint face,
        uint depthSlice,
        uint tileX,
        uint tileY)
    {
        IReadOnlyList<TextureMipTile> mipTiles = tiles;
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
    public static TextureMipTile[] Bootstrap(in TextureDesc description, SomeEngine.Graphics.TextureViewDimension sampledDimension, IReadOnlyList<TextureMipTile> mipTiles)
    {
        uint mipLevel = description.MipLevelCount - 1;
        TextureMipTile[] tiles = mipTiles
            .Where(tile => tile.MipLevel == mipLevel)
            .ToArray();
        if (tiles.Length == 0)
            throw new InvalidDataException("Texture metadata does not contain its coarsest Bootstrap mip.");

        bool cube = sampledDimension is
            SomeEngine.Graphics.TextureViewDimension.Cube or
            SomeEngine.Graphics.TextureViewDimension.CubeArray;
        uint faceCount = cube ? 6u : 1u;
        uint depthCount = description.Dimension == SomeEngine.Graphics.TextureDimension.Texture3D
            ? MipExtent(description.Depth, mipLevel)
            : 1u;
        uint width = MipExtent(description.Width, mipLevel);
        uint height = MipExtent(description.Height, mipLevel);
        ulong requiredArea = checked((ulong)width * height);

        for (uint layer = 0; layer < (cube ? description.ArrayLayerCount / 6 : description.ArrayLayerCount); layer++)
        for (uint face = 0; face < faceCount; face++)
        for (uint depth = 0; depth < depthCount; depth++)
        {
            TextureMipTile[] group = tiles
                .Where(tile => tile.ArrayLayer == layer &&
                    tile.Face == face &&
                    tile.DepthSlice == depth)
                .ToArray();
            if (group.Length == 0)
            {
                throw new InvalidDataException(
                    $"Texture Bootstrap mip {mipLevel} is missing layer {layer}, face {face}, " +
                    $"depth slice {depth}.");
            }

            ulong area = 0;
            for (int index = 0; index < group.Length; index++)
            {
                TextureMipTile tile = group[index];
                ulong tileArea = (ulong)tile.Width * tile.Height;
                if (area > ulong.MaxValue - tileArea)
                    throw new InvalidDataException("Texture Bootstrap tile area exceeds the supported range.");
                area += tileArea;
                for (int previous = 0; previous < index; previous++)
                {
                    TextureMipTile other = group[previous];
                    bool overlap = tile.TileX < other.TileX + other.Width &&
                        other.TileX < tile.TileX + tile.Width &&
                        tile.TileY < other.TileY + other.Height &&
                        other.TileY < tile.TileY + tile.Height;
                    if (overlap)
                        throw new InvalidDataException("Texture Bootstrap tiles overlap.");
                }
            }
            if (area != requiredArea)
                throw new InvalidDataException("Texture Bootstrap tiles do not cover the coarsest mip.");
        }
        return tiles;
    }

}
