using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".shader.asset")]
public sealed partial class Shader
{
    public partial string? Name { get; set; }
    public partial ImportTrace? ImportTrace { get; set; }
    public partial ImmutableList<CookedShaderProgram>? Programs { get; set; }
    public partial ImmutableList<ShaderEntryPointAttribute>? EntryPointAttributes { get; set; }
    public partial ImmutableList<BackendReflection>? Reflections { get; set; }
    public partial ImmutableList<ShaderEntryPointReflection>? EntryPointReflections { get; set; }
    public partial ShaderMetadata? Metadata { get; set; }
    public partial ImmutableList<ShaderEntryPointMetadata>? EntryPointMetadata { get; set; }
    public partial uint SchemaVersion { get; set; }
}
