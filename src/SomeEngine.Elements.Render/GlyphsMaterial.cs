using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Elements.Render;

/// <summary>The built-in material for single-channel SDF glyphs.</summary>
public sealed class GlyphsMaterial : Material
{
    public static Guid TypeId { get; } = new("94e82e6c-1a9d-4bf1-a81f-96d65e433d61");
    public override IReadOnlyList<MaterialPropertyDefinition> Properties => [];

    /// <summary>Uses the cooked built-in shader for the requested graphics target.</summary>
    public static GlyphsMaterial Create(SlangCompileTarget target) => new()
    {
        Shader = ElementShaders.Get("Glyphs", target),
    };

    /// <summary>Uses cooked code with the application's existing Slang compiler context.</summary>
    public static GlyphsMaterial Create(SlangCompileTarget target, IGlobalSession global) => new()
    {
        Shader = ElementShaders.Get("Glyphs", target, global),
    };

}
