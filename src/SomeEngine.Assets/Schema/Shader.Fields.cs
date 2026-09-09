using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public sealed partial class Shader
{
    private ShaderData _content = new();

    public string? Name { get => _content.Name; set => _content.Name = value; }
    public ImportTrace? ImportTrace { get => _content.ImportTrace; set => _content.ImportTrace = value; }
    public IList<CookedShaderProgram>? Programs { get => _content.Programs; set => _content.Programs = value; }
    public IList<ShaderEntryPointAttribute>? EntryPointAttributes { get => _content.EntryPointAttributes; set => _content.EntryPointAttributes = value; }
    public IList<BackendReflection>? Reflections { get => _content.Reflections; set => _content.Reflections = value; }
    public IList<ShaderEntryPointReflection>? EntryPointReflections { get => _content.EntryPointReflections; set => _content.EntryPointReflections = value; }
    public ShaderMetadata? Metadata { get => _content.Metadata; set => _content.Metadata = value; }
    public IList<ShaderEntryPointMetadata>? EntryPointMetadata { get => _content.EntryPointMetadata; set => _content.EntryPointMetadata = value; }
    public uint SchemaVersion { get => _content.SchemaVersion; set => _content.SchemaVersion = value; }
}
