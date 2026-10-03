namespace SomeEngine.Core.Math;

/// <summary>Linear red, green, blue and alpha values.</summary>
public readonly record struct Color(float R, float G, float B, float A = 1);

public readonly record struct Thickness(float Left, float Top, float Right, float Bottom);

public readonly record struct Rect(float X, float Y, float Width, float Height);
