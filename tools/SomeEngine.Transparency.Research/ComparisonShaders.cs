using SomeEngine.Resources;

namespace SomeEngine.Transparency.Research;

public sealed class ComparisonShaders(string? blendResolveFile = null)
{
    private readonly Dictionary<(string File, string Entries, string Definitions), Shader> _shaders = [];

    public Shader Get(string file, string[] entries, string definitions = "")
    {
        if (blendResolveFile is not null && file == "blend_effect_cells.slang") definitions += "#define BLEND_DEPTH_INTERVALS 1\n";
        var key = (file, string.Join(',', entries), definitions);
        if (!_shaders.TryGetValue(key, out Shader? shader))
        {
            shader = ResearchShader.Compile(file == "blend_effect_resolve.slang" && blendResolveFile is not null
                ? blendResolveFile : file, entries, definitions: definitions);
            _shaders.Add(key, shader);
        }
        return shader;
    }
}
