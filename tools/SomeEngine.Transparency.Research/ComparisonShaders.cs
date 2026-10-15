using SomeEngine.Resources;

namespace SomeEngine.Transparency.Research;

public sealed class ComparisonShaders
{
    private readonly Dictionary<(string File, string Entries, string Definitions), Shader> _shaders = [];

    public Shader Get(string file, string[] entries, string definitions = "")
    {
        var key = (file, string.Join(',', entries), definitions);
        if (!_shaders.TryGetValue(key, out Shader? shader))
        {
            shader = ResearchShader.Compile(file, entries, definitions: definitions);
            _shaders.Add(key, shader);
        }
        return shader;
    }
}
