namespace SomeEngine.Resources;

/// <summary>An entry in a shader whose required bytecode is already available.</summary>
public sealed record ShaderEntry(Shader Shader, string EntryPoint, ShaderStage Stage);
