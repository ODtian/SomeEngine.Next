using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Elements.Render;

/// <summary>Scene color, depth and RenderWorld view used to draw world canvases.</summary>
public readonly record struct WorldCanvasTarget(GraphTextureId Color, GraphTextureId Depth, Entity View,
    Format DepthFormat = Format.D32Float, CompareOperation DepthComparison = CompareOperation.LessOrEqual);

internal readonly record struct CanvasPassTarget(GraphTextureId Color, GraphTextureId Depth,
    Format DepthFormat, CompareOperation DepthComparison, bool WorldSpace);
