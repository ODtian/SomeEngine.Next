namespace SomeEngine.Render.Shadows;

public sealed partial record VirtualShadowMapSettings
{
    public bool FirstPersonShadowsEnabled { get; init; } = true;
    public int FirstPersonFirstClipmapLevel { get; init; } = 8;
    public int FirstPersonLastClipmapLevel { get; init; } = 18;
    public uint MaximumViewsPerLight => Math.Max(6u, checked(DirectionalClipmapLevels +
        (FirstPersonShadowsEnabled ? (uint)(FirstPersonLastClipmapLevel - FirstPersonFirstClipmapLevel + 1) : 0u)));
    public bool CullBackfacingPixels { get; init; } = true;
    public float FirstPersonPixelRequestBias { get; init; } = 2f;
    public int FirstPersonPixelRequestMinLevel { get; init; } = 8;
    public float DirectionalPageDilationBorder { get; init; } = .05f;
    public float LocalPageDilationBorder { get; init; } = .05f;
}
