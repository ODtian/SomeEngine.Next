namespace SomeEngine.Graphics.Direct3D12;

internal sealed partial class D3D12Backend : IBackendShaderTarget
{
    SlangShaderSharp.SlangCompileTarget IBackendShaderTarget.ShaderTarget => SlangShaderSharp.SlangCompileTarget.Dxil;

    private sealed partial class D3D12Device : IMemoryBarrierSupport
    {
        internal object PipelineCacheMergeGate { get; } = new();
        bool IMemoryBarrierSupport.SplitMemoryBarriers => FeatureSupport.EnhancedBarriers;
    }
}
