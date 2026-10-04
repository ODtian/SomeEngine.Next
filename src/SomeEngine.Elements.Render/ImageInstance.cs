using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render;

/// <summary>An image's fitted rectangle, texture coordinates and canvas drawing order.</summary>
public readonly record struct ImageInstance(
    Entity Node,
    Texture? Texture,
    Rect Rectangle,
    Rect Uv,
    Rect ClipRectangle,
    int DrawOrder,
    uint Version,
    ulong TextureRevision = 0) : IBufferElement
{
    public Matrix3x2 Matrix { get; init; } = Matrix3x2.Identity;
    public float Alpha { get; init; } = 1;
}
