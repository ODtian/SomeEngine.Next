using SlangShaderSharp;

namespace SomeEngine.Graphics;

internal interface IBackendShaderTarget
{
    SlangCompileTarget ShaderTarget { get; }
}
