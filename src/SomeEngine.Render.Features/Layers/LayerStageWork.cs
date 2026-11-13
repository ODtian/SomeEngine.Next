using System.Runtime.InteropServices;

namespace SomeEngine.Render.Features.Layers;

/// <summary>One layer tile and the visible clusters used by a geometry raster stage.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly record struct LayerStageWork(uint Tile, uint RasterStage, uint ClusterOffset, uint ClusterCount);
