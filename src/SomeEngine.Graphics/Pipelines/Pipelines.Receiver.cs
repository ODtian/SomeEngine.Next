using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null);

    Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null);

    Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null) =>
        Receiver.CreateGraphicsPipeline(device, desc, cache);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null) =>
        Receiver.CreateComputePipeline(device, desc, cache);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null) =>
        Receiver.CreateMeshPipeline(device, desc, cache);
}
