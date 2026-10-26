using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Job;
using TextureAsset = SomeEngine.Assets.Schema.TextureAsset;

namespace SomeEngine.Assets.Importers;

/// <summary>
/// Builds the conservative min/max texture consumed by TextureDisplacement.BoundsMap.
/// Each mip texel contains the exact range of source samples covered by its normalized
/// texture footprint; odd dimensions are partitioned proportionally rather than dropping
/// the final source row or column.
/// </summary>
public static class DisplacementBoundsTexture
{
    public static TextureAsset Create(
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
        sourceRange = ReadSampleRanges(samples, current);

        using var pixels = new TextureBlock
        {
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Width = width, Height = height, Depth = 1, ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R32G32Float,
            SampledFormat = SomeEngine.Graphics.Format.R32G32Float,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        var tiles = new List<TextureMipTile>();
        uint level = 0;
        uint currentWidth = width;
        uint currentHeight = height;
        while (true)
        {
            tiles.Add(CreateTile(pixels, level, currentWidth, currentHeight, current));
            if (currentWidth == 1u && currentHeight == 1u)
                break;

            uint nextWidth = Math.Max(1u, currentWidth >> 1);
            uint nextHeight = Math.Max(1u, currentHeight >> 1);
            var next = new Vector2[checked((int)((ulong)nextWidth * nextHeight))];
            JobSystem.ExecuteParallel(new BuildMipRows(current, currentWidth, currentHeight,
                next, nextWidth, nextHeight), (int)nextHeight);

            current = next;
            currentWidth = nextWidth;
            currentHeight = nextHeight;
            level++;
        }

        pixels.MipLevelCount = checked((uint)tiles.Count);
        pixels.MipTiles = tiles;
        var asset = new TextureAsset(assetGuid) { Name = name };
        try { asset.SetBlock(pixels); return asset; }
        catch { asset.Dispose(); throw; }
    }

    private const int SampleBatchSize = 4096;

    private static unsafe Vector2 ReadSampleRanges(ReadOnlySpan<float> samples, Vector2[] values)
    {
        Vector2[] ranges = new Vector2[(samples.Length - 1) / SampleBatchSize + 1];
        fixed (float* source = samples)
            JobSystem.ExecuteParallel(new ReduceSamples(source, samples.Length, values, ranges), ranges.Length);
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        foreach (Vector2 range in ranges)
        {
            minimum = MathF.Min(minimum, range.X);
            maximum = MathF.Max(maximum, range.Y);
        }
        return new(minimum, maximum);
    }

    private readonly unsafe struct ReduceSamples(float* samples, int count, Vector2[] values, Vector2[] ranges) : IJobParallelFor
    {
        public void Execute(int index)
        {
            int start = index * SampleBatchSize;
            int end = Math.Min(count, start + SampleBatchSize);
            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;
            for (int sample = start; sample < end; sample++)
            {
                float value = samples[sample];
                if (!float.IsFinite(value))
                    throw new ArgumentOutOfRangeException(nameof(samples), "Displacement samples must be finite.");
                values[sample] = new Vector2(value, value);
                minimum = MathF.Min(minimum, value);
                maximum = MathF.Max(maximum, value);
            }
            ranges[index] = new(minimum, maximum);
        }
    }

    private readonly record struct BuildMipRows(Vector2[] SourceRanges, uint SourceWidth, uint SourceHeight,
        Vector2[] OutputRanges, uint Width, uint Height) : IJobParallelFor
    {
        public void Execute(int row)
        {
            uint y = (uint)row;
            uint sourceY0 = checked((uint)((ulong)y * SourceHeight / Height));
            uint sourceY1 = checked((uint)(((ulong)(y + 1u) * SourceHeight + Height - 1u) / Height));
            sourceY1 = Math.Min(sourceY1, SourceHeight);
            for (uint x = 0; x < Width; x++)
            {
                uint sourceX0 = checked((uint)((ulong)x * SourceWidth / Width));
                uint sourceX1 = checked((uint)(((ulong)(x + 1u) * SourceWidth + Width - 1u) / Width));
                sourceX1 = Math.Min(sourceX1, SourceWidth);
                float minimum = float.PositiveInfinity;
                float maximum = float.NegativeInfinity;
                for (uint sourceY = sourceY0; sourceY < sourceY1; sourceY++)
                for (uint sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                {
                    Vector2 range = SourceRanges[checked((int)((ulong)sourceY * SourceWidth + sourceX))];
                    minimum = MathF.Min(minimum, range.X);
                    maximum = MathF.Max(maximum, range.Y);
                }
                OutputRanges[checked((int)((ulong)y * Width + x))] = new Vector2(minimum, maximum);
            }
        }
    }

    private static TextureMipTile CreateTile(
        TextureBlock pixels,
        uint level,
        uint width,
        uint height,
        Vector2[] ranges)
    {
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(ranges.AsSpan());
        TextureMipTile tile = TextureTiles.Describe(new TextureMipTile
        {
            MipLevel = level,
            TileX = 0,
            TileY = 0,
            Width = width,
            Height = height,
            RowPitch = checked((ulong)width * 8u),
            SlicePitch = checked((ulong)width * height * 8u),
        }, payload.Length);
        pixels.SetBytes(tile.ChunkKey, payload.ToArray());
        return tile;
    }
}
