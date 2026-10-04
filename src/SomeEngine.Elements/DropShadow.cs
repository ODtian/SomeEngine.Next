using System.Numerics;
using SomeEngine.Core.Math;

namespace SomeEngine.Elements;

/// <summary>A surface shadow in linear RGBA and canvas layout units.</summary>
public readonly record struct DropShadow(Color Color, Vector2 Offset, float BlurRadius);
