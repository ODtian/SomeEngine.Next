namespace SomeEngine.Graphics.Direct3D12;

public sealed record Options
{
    public bool UseWarpAdapter { get; init; }
    public bool EnableDebugLayer { get; init; } = true;
    public bool EnableGpuValidation { get; init; }
    /// <summary>CBV/SRV/UAV descriptors available to one command-list recording.</summary>
    public int ResourceDescriptorsPerCommandList { get; init; } = 4096;
    /// <summary>Sampler descriptors available to one command-list recording.</summary>
    public int SamplerDescriptorsPerCommandList { get; init; } = 256;
    /// <summary>Optional versioned ID3D12PipelineLibrary artifact path for cross-session PSO reuse.</summary>
    public string? PipelineCachePath { get; init; }
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
