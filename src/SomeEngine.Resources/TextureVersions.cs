namespace SomeEngine.Resources;

/// <summary>Whole content, available pixels, and the content selected for device upload.</summary>
public readonly record struct TextureVersions(ulong Content, ulong Pixels, ulong Replacement);
