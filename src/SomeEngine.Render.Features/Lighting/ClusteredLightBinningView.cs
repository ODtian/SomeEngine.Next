using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>View projection and logarithmic cell dimensions shared with light binning.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct ClusteredLightBinningView
{
    public const int SizeInBytes = 256;

    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public uint TileSizeX;
    public uint TileSizeY;
    public uint TileCountX;
    public uint TileCountY;
    public Vector4 ZParams;
    public uint DepthSliceCount;
    public uint MaxLightsPerCell;
    public uint DirectionalCount;
    public uint NonDirectionalCount;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint CellCountPerView;
    public uint Padding;
    public Matrix4x4 InverseProjection;

    public static ClusteredLightBinningView Create(in RenderView view, ClusteredLightGridSettings settings,
        uint directionalCount, uint nonDirectionalCount)
    {
        const float nearDepth = 0.1f;
        const float farDepth = 10_000f;
        float sliceScale = settings.DepthSliceCount / MathF.Log2(farDepth / nearDepth);
        uint tileCountX = (view.ViewportWidth + settings.TileSize - 1u) / settings.TileSize;
        uint tileCountY = (view.ViewportHeight + settings.TileSize - 1u) / settings.TileSize;
        Matrix4x4.Invert(view.Projection, out Matrix4x4 inverseProjection);
        return new()
        {
            View = view.View,
            Projection = view.Projection,
            TileSizeX = settings.TileSize,
            TileSizeY = settings.TileSize,
            TileCountX = tileCountX,
            TileCountY = tileCountY,
            ZParams = new(nearDepth, farDepth, sliceScale, -MathF.Log2(nearDepth) * sliceScale),
            DepthSliceCount = settings.DepthSliceCount,
            MaxLightsPerCell = settings.MaxLightsPerCell,
            DirectionalCount = directionalCount,
            NonDirectionalCount = nonDirectionalCount,
            ScreenWidth = view.ViewportWidth,
            ScreenHeight = view.ViewportHeight,
            CellCountPerView = tileCountX * tileCountY * settings.DepthSliceCount,
            InverseProjection = inverseProjection,
        };
    }
}
