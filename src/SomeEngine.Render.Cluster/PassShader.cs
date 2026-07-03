using SomeEngine.Assets;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Cluster;

internal readonly record struct PassShader(Handle<Shader> Shader, string? EntryPoint)
{
    public bool IsEmpty => !Shader.IsValid || string.IsNullOrWhiteSpace(EntryPoint);
}


