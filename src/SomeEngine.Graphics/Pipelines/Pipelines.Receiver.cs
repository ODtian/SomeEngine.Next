namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null);

    /// <summary>
    /// Creates a Graphics Pipeline without performing native Pipeline creation on the caller's
    /// thread. A successfully completed Task returns a fully usable Pipeline.
    /// </summary>
    Task<Pipeline> CreateGraphicsPipelineAsync(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null);

    Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null);

    /// <summary>
    /// Creates a Compute Pipeline without performing native Pipeline creation on the caller's
    /// thread. A successfully completed Task returns a fully usable Pipeline.
    /// </summary>
    Task<Pipeline> CreateComputePipelineAsync(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null);

    Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null);

    /// <summary>
    /// Creates a Mesh Pipeline without performing native Pipeline creation on the caller's
    /// thread. A successfully completed Task returns a fully usable Pipeline.
    /// </summary>
    Task<Pipeline> CreateMeshPipelineAsync(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null);
}
