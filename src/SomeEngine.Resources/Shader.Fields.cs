using SomeEngine.Graphics;
using System.Collections.Immutable;

namespace SomeEngine.Resources;


public sealed partial class Shader
{
    public string? Name { get; set; }
    public ImmutableList<SlangShaderSharp.ShaderProgram?>? Programs { get; set; }
    public ImmutableList<ShaderEntryPointAttribute>? EntryPointAttributes { get; set; }
    public ImmutableList<BackendReflection>? Reflections { get; set; }
    public ImmutableList<ShaderEntryPointReflection>? EntryPointReflections { get; set; }
    public ShaderMetadata? Metadata { get; set; }
    public ImmutableList<ShaderEntryPointMetadata>? EntryPointMetadata { get; set; }
    public uint SchemaVersion { get; set; }
}
