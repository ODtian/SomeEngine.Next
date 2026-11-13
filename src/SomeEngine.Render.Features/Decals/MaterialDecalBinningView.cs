using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Features.Decals;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MaterialDecalBinningView
{
    internal const uint SizeInBytes = 240;

    internal Matrix4x4 View;
    internal Matrix4x4 Projection;
    internal Matrix4x4 InverseProjection;
    internal uint ScreenWidth, ScreenHeight;
    internal uint TileCountX, TileCountY;
    internal uint TileSize, DepthSliceCount, CellCount, DecalCount;
    internal Vector4 Depth;

    internal static MaterialDecalBinningView Create(in RenderView view,
        MaterialDecalBinningSettings settings, uint cellCount, uint decalCount)
    {
        Matrix4x4.Invert(view.Projection, out Matrix4x4 inverseProjection);
        float scale = settings.DepthSliceCount / MathF.Log2(settings.FarDepth / settings.NearDepth);
        return new()
        {
            View = view.View,
            Projection = view.Projection,
            InverseProjection = inverseProjection,
            ScreenWidth = view.ViewportWidth,
            ScreenHeight = view.ViewportHeight,
            TileCountX = (view.ViewportWidth + settings.TileSize - 1) / settings.TileSize,
            TileCountY = (view.ViewportHeight + settings.TileSize - 1) / settings.TileSize,
            TileSize = settings.TileSize,
            DepthSliceCount = settings.DepthSliceCount,
            CellCount = cellCount,
            DecalCount = decalCount,
            Depth = new(settings.NearDepth, settings.FarDepth, scale, -MathF.Log2(settings.NearDepth) * scale),
        };
    }
}
