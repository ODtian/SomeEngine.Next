using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render;

/// <summary>The built-in material for single-channel SDF glyphs.</summary>
public sealed class GlyphsMaterial : Material
{
    public static Guid TypeId { get; } = new("94e82e6c-1a9d-4bf1-a81f-96d65e433d61");
    public override Guid MaterialTypeId => TypeId;
    public override IReadOnlyList<MaterialPropertyDefinition> Properties => [];

    /// <summary>Compiles the built-in Slang shader once for the requested graphics target.</summary>
    public static GlyphsMaterial Create(SlangCompileTarget target) => new()
    {
        Shader = ElementShaders.Get("Glyphs", target),
    };

    public override void ApplyParameter(MaterialValue parameter, Texture? texture) =>
        DevelopmentCheck.Fail("Glyphs drawing values are supplied by the canvas instance buffer.");

    protected override void CopyProperties(Material source) { }
    protected override Material CreateEmpty() => new GlyphsMaterial();
}
