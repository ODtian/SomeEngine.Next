using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Typography;

namespace SomeEngine.Elements.Render;

/// <summary>The CPU glyph atlas used by this World's text extraction.</summary>
public readonly record struct TextAtlas(GlyphAtlas Atlas) : IComponent;

/// <summary>The render atlas entity supplying a canvas's glyph pages.</summary>
public readonly record struct CanvasGlyphAtlas(Entity Atlas) : IComponent;

/// <summary>Detached single-channel pixels for one atlas page.</summary>
public readonly record struct GlyphPagePixels(int Width, int Height, ReadOnlyMemory<byte> Pixels);

/// <summary>Read-only atlas pixels and the regions changed by the latest extraction.</summary>
public readonly record struct RenderGlyphAtlas(ReadOnlyMemory<GlyphPagePixels> Pages,
    ReadOnlyMemory<AtlasDirtyRegion> DirtyRegions, int DistanceSpread, uint Version) : IComponent;

/// <summary>A visible glyph in canvas coordinates. GlyphIndex refers to its shaped text position.</summary>
public readonly record struct GlyphInstance(Entity Node, int GlyphIndex, Rect Rectangle,
    Vector2 UV0, Vector2 UV1, Color Color, Rect ClipRectangle, int PageIndex,
    int DrawOrder, uint Version) : IBufferElement;
