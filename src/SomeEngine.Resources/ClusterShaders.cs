using System.Collections.Immutable;

namespace SomeEngine.Resources;

public sealed record ShaderOperation
{
    public ClusterShaderOperationRole Role { get; init; }
    public ImmutableList<ShaderEntry> Shaders { get; init; } = [];
}

/// <summary>Ready cluster operations. This object disposes the distinct shaders supplied to it.</summary>
public sealed class ClusterShaders : IDisposable
{
    private int _disposed;
    public string? Name { get; init; }
    public ImmutableList<ShaderOperation> Operations { get; init; } = [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (Shader shader in Operations.SelectMany(static operation => operation.Shaders)
                     .Select(static entry => entry.Shader).Distinct())
            shader.Dispose();
    }

    public static bool IsPositionOperation(ClusterShaderOperationRole role)
        => role is ClusterShaderOperationRole.SoftwareVisibilityRaster or ClusterShaderOperationRole.HardwareVisibilityRaster;
}
