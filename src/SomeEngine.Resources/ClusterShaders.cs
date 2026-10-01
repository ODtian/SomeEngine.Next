using System.Collections.Immutable;
using SlangShaderSharp;

namespace SomeEngine.Resources;

public sealed record ShaderOperation
{
    public ClusterShaderOperationRole Role { get; init; }
    public ImmutableList<Shader> Shaders { get; init; } = [];
    public ImmutableList<EntryPointReflection> Entries { get; init; } = [];
}

/// <summary>Ready cluster operations backed by immutable shader references.</summary>
public sealed class ClusterShaders
{
    public string? Name { get; init; }
    public ImmutableList<ShaderOperation> Operations { get; init; } = [];

    public static bool IsPositionOperation(ClusterShaderOperationRole role)
        => role is ClusterShaderOperationRole.SoftwareVisibilityRaster or ClusterShaderOperationRole.HardwareVisibilityRaster;
}
