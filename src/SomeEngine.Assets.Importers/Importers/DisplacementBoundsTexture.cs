using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using TextureData = SomeEngine.Assets.Schema.TextureData;

namespace SomeEngine.Assets.Importers;

/// <summary>
/// Builds the conservative min/max texture consumed by TextureDisplacement.BoundsMap.
/// Each mip texel contains the exact range of source samples covered by its normalized
/// texture footprint; odd dimensions are partitioned proportionally rather than dropping
/// the final source row or column.
/// </summary>
public static class DisplacementBoundsTexture
{
    public static TextureData Create(
        Guid assetGuid,
        string name,
        uint width,
        uint height,
        ReadOnlySpan<float> samples,
        out Vector2 sourceRange)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        int sampleCount = checked((int)((ulong)width * height));
        if (samples.Length != sampleCount)
            throw new ArgumentException("Displacement sample count must exactly match width * height.", nameof(samples));

        var current = new Vector2[sampleCount];
        float sourceMinimum = float.PositiveInfinity;
        float sourceMaximum = float.NegativeInfinity;
        for (int index = 0; index < samples.Length; index++)
        {
            float value = samples[index];
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(samples), "Displacement samples must be finite.");
            current[index] = new Vector2(value, value);
            sourceMinimum = MathF.Min(sourceMinimum, value);
            sourceMaximum = MathF.Max(sourceMaximum, value);
        }
        sourceRange = new Vector2(sourceMinimum, sourceMaximum);

        var tiles = new List<TextureMipTile>();
        uint level = 0;
        uint currentWidth = width;
        uint currentHeight = height;
        while (true)
        {
            tiles.Add(CreateTile(level, currentWidth, currentHeight, current));
            if (currentWidth == 1u && currentHeight == 1u)
                break;

            uint nextWidth = Math.Max(1u, currentWidth >> 1);
            uint nextHeight = Math.Max(1u, currentHeight >> 1);
            var next = new Vector2[checked((int)((ulong)nextWidth * nextHeight))];
            for (uint y = 0; y < nextHeight; y++)
            {
                uint sourceY0 = checked((uint)((ulong)y * currentHeight / nextHeight));
                uint sourceY1 = checked((uint)(((ulong)(y + 1u) * currentHeight + nextHeight - 1u) / nextHeight));
                sourceY1 = Math.Min(sourceY1, currentHeight);
                for (uint x = 0; x < nextWidth; x++)
                {
                    uint sourceX0 = checked((uint)((ulong)x * currentWidth / nextWidth));
                    uint sourceX1 = checked((uint)(((ulong)(x + 1u) * currentWidth + nextWidth - 1u) / nextWidth));
                    sourceX1 = Math.Min(sourceX1, currentWidth);

                    float minimum = float.PositiveInfinity;
                    float maximum = float.NegativeInfinity;
                    for (uint sourceY = sourceY0; sourceY < sourceY1; sourceY++)
                    {
                        for (uint sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                        {
                            Vector2 range = current[checked((int)((ulong)sourceY * currentWidth + sourceX))];
                            minimum = MathF.Min(minimum, range.X);
                            maximum = MathF.Max(maximum, range.Y);
                        }
                    }
                    next[checked((int)((ulong)y * nextWidth + x))] = new Vector2(minimum, maximum);
                }
            }

            current = next;
            currentWidth = nextWidth;
            currentHeight = nextHeight;
            level++;
        }

        return new TextureData
        {
            AssetGuid = assetGuid.ToString(),
            Name = name,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Width = width,
            Height = height,
            Depth = 1,
            MipLevelCount = checked((uint)tiles.Count),
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R32G32Float,
            SampledFormat = SomeEngine.Graphics.Format.R32G32Float,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = tiles,
        };
    }

    private static TextureMipTile CreateTile(
        uint level,
        uint width,
        uint height,
        Vector2[] ranges)
    {
        byte[] payload = MemoryMarshal.AsBytes(ranges.AsSpan()).ToArray();
        return new TextureMipTile
        {
            MipLevel = level,
            TileX = 0,
            TileY = 0,
            Width = width,
            Height = height,
            RowPitch = checked((ulong)width * 8u),
            SlicePitch = checked((ulong)width * height * 8u),
            Payload = payload,
        };
    }
}
