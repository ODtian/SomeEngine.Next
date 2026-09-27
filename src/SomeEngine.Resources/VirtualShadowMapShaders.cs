namespace SomeEngine.Resources;

/// <summary>Ready shadow-map entries. This object disposes the distinct shaders supplied to it.</summary>
public sealed class VirtualShadowMapShaders : IDisposable
{
    private int _disposed;
    public string? Name { get; init; }
    public ShaderEntry? ResetReceiverMasks { get; init; }
    public ShaderEntry? ResetFrame { get; init; }
    public ShaderEntry? MarkPages { get; init; }
    public ShaderEntry? MarkCoarsePages { get; init; }
    public ShaderEntry? AllocatePages { get; init; }
    public ShaderEntry? ClearPages { get; init; }
    public ShaderEntry? InitializeState { get; init; }
    public ShaderEntry? InvalidateCasters { get; init; }
    public ShaderEntry? BuildPagePool { get; init; }
    public ShaderEntry? CompactViews { get; init; }
    public ShaderEntry? InitializePageHiZ { get; init; }
    public ShaderEntry? ReducePageHiZ { get; init; }
    public ShaderEntry? DynamicResolution { get; init; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ShaderEntry?[] entries = [ResetReceiverMasks, ResetFrame, MarkPages, MarkCoarsePages, AllocatePages, ClearPages,
            InitializeState, InvalidateCasters, BuildPagePool, CompactViews, InitializePageHiZ,
            ReducePageHiZ, DynamicResolution];
        foreach (Shader shader in entries.OfType<ShaderEntry>().Select(static entry => entry.Shader).Distinct())
            shader.Dispose();
    }
}
