namespace SomeEngine.Resources;

/// <summary>Whole content, available pixels, upload content, and device allocation changes.</summary>
public readonly record struct TextureVersions(ulong Content, ulong Pixels, ulong Replacement, ulong Allocation = 0);
