using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class NullLifetimeAndSubmissionTests
{
    [Fact]
    public void Finished_command_list_pins_expanded_pipeline_dependencies_until_discard()
    {
        using Device device = new();
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(Shader(ShaderStage.Compute, 1));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, shader));

        CommandListHandle commands;
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Compute))
        {
            context.SetPipeline(pipeline);
            context.Dispatch(1, 1, 1);
            commands = context.Finish();
        }

        Assert.Throws<InvalidOperationException>(() => device.DestroyPipeline(pipeline));
        Assert.Throws<InvalidOperationException>(() => device.DestroyPipelineLayout(layout));
        Assert.Throws<InvalidOperationException>(() => device.DestroyShader(shader));

        device.DiscardCommandList(commands);

        Assert.Throws<InvalidOperationException>(() => device.DestroyPipelineLayout(layout));
        Assert.Throws<InvalidOperationException>(() => device.DestroyShader(shader));
        device.DestroyPipeline(pipeline);
        device.DestroyPipelineLayout(layout);
        device.DestroyShader(shader);
    }

    [Fact]
    public void Successful_submit_converts_unpublished_pins_to_exact_queue_use()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferHandle source = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination),
            MemoryType.Readback);

        CommandListHandle commands;
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Copy))
        {
            context.CopyBuffer(source, 0, destination, 0, 16);
            commands = context.Finish();
        }

        Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(source));
        Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(destination));

        GpuCompletion completion = device.Submit(QueueType.Copy, [commands]);
        device.DestroyBuffer(source);
        device.DestroyBuffer(destination);
        Assert.Equal(0, device.CollectGarbage());

        device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 3);
    }

    [Fact]
    public void Bind_group_children_block_layout_views_resources_and_samplers()
    {
        using Device device = new();
        BufferHandle buffer = device.CreateBuffer(new BufferDesc(64, BufferUsage.ShaderWrite));
        BufferViewHandle view = device.CreateBufferView(new BufferViewDesc(
            buffer,
            BufferRange.Whole,
            BindingKind.StorageBuffer));
        SamplerHandle sampler = device.CreateSampler(new SamplerDesc());
        BindGroupLayoutHandle layout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.Sampler, 1, ShaderStage.Compute),
        ]);
        BindGroupHandle group = device.CreateBindGroup(layout, [
            BindingWrite.Buffer(0, view),
            BindingWrite.SamplerValue(1, sampler),
        ]);

        Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(buffer));
        Assert.Throws<InvalidOperationException>(() => device.DestroyBufferView(view));
        Assert.Throws<InvalidOperationException>(() => device.DestroySampler(sampler));
        Assert.Throws<InvalidOperationException>(() => device.DestroyBindGroupLayout(layout));

        device.DestroyBindGroup(group);
        Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(buffer));
        device.DestroyBufferView(view);
        device.DestroyBuffer(buffer);
        device.DestroySampler(sampler);
        device.DestroyBindGroupLayout(layout);
    }

    [Fact]
    public void Texture_view_blocks_its_texture_and_failed_destroy_preserves_both_handles()
    {
        using Device device = new();
        TextureHandle texture = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled));
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ShaderResource));

        Assert.Throws<InvalidOperationException>(() => device.DestroyTexture(texture));
        device.DestroyTextureView(view);
        device.DestroyTexture(texture);
    }

    [Fact]
    public void Pipeline_metadata_and_parent_lifetimes_follow_the_exact_shader_set()
    {
        using Device device = new();
        BindGroupLayoutHandle groupLayout = device.CreateBindGroupLayout([]);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { groupLayout },
            Array.Empty<PushConstantRange>()));
        ShaderHandle vertex = device.CreateShader(Shader(ShaderStage.Vertex, 11));
        ShaderHandle pixel = device.CreateShader(Shader(ShaderStage.Pixel, 12));
        ShaderHandle compute = device.CreateShader(Shader(ShaderStage.Compute, 13));
        PipelineHandle raster = device.CreateRasterPipeline(new RasterPipelineDesc(
            layout,
            vertex,
            pixel,
            new[] { Format.R8G8B8A8UNorm }));
        PipelineHandle computePipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, compute));

        PipelineMetadata rasterMetadata = device.GetPipelineMetadata(raster);
        Assert.Equal(PipelineType.Raster, rasterMetadata.Type);
        Assert.Equal(2, rasterMetadata.Shaders.Count);
        Assert.Equal(new PipelineShaderIdentity(ShaderKey(11), ShaderStage.Vertex), rasterMetadata.Shaders[0]);
        Assert.Equal(new PipelineShaderIdentity(ShaderKey(12), ShaderStage.Pixel), rasterMetadata.Shaders[1]);

        PipelineMetadata computeMetadata = device.GetPipelineMetadata(computePipeline);
        Assert.Equal(PipelineType.Compute, computeMetadata.Type);
        Assert.Single(computeMetadata.Shaders);
        Assert.Equal(new PipelineShaderIdentity(ShaderKey(13), ShaderStage.Compute), computeMetadata.Shaders[0]);

        Assert.Throws<InvalidOperationException>(() => device.DestroyPipelineLayout(layout));
        Assert.Throws<InvalidOperationException>(() => device.DestroyBindGroupLayout(groupLayout));
        Assert.Throws<InvalidOperationException>(() => device.DestroyShader(vertex));
        Assert.Throws<InvalidOperationException>(() => device.DestroyShader(pixel));
        Assert.Throws<InvalidOperationException>(() => device.DestroyShader(compute));

        device.DestroyPipeline(raster);
        Assert.Throws<InvalidOperationException>(() => device.DestroyPipelineLayout(layout));
        device.DestroyShader(vertex);
        device.DestroyShader(pixel);
        device.DestroyPipeline(computePipeline);
        device.DestroyShader(compute);
        device.DestroyPipelineLayout(layout);
        device.DestroyBindGroupLayout(groupLayout);
    }

    [Fact]
    public void Invalid_later_command_list_rolls_back_all_prior_state_bytes_and_statistics()
    {
        using Device device = new();
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle intermediate = device.CreateBuffer(new BufferDesc(
            16,
            BufferUsage.CopySource | BufferUsage.CopyDestination));
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination),
            MemoryType.Readback);
        byte[] expected = Enumerable.Range(1, 16).Select(static value => checked((byte)value)).ToArray();
        device.WriteBuffer(upload, 0, expected);

        CommandListHandle first;
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Copy))
        {
            context.Barriers([
                ResourceBarrier.Transition(intermediate.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            context.CopyBuffer(upload, 0, intermediate, 0, 16);
            context.Barriers([
                ResourceBarrier.Transition(intermediate.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            context.CopyBuffer(intermediate, 0, readback, 0, 16);
            first = context.Finish();
        }

        CommandListHandle second;
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Copy))
        {
            context.Barriers([
                ResourceBarrier.Transition(intermediate.Resource, ResourceState.Common, ResourceState.Common),
            ]);
            second = context.Finish();
        }

        Assert.Throws<InvalidOperationException>(() => device.Submit(QueueType.Copy, [first, second]));
        Assert.Equal(0, device.Statistics.Submissions);
        Assert.Equal(0, device.Statistics.SubmittedCommandLists);
        Assert.Equal(0, device.Statistics.ExecutedCopies);

        byte[] untouched = new byte[16];
        device.ReadBuffer(readback, 0, untouched);
        Assert.All(untouched, static value => Assert.Equal(0, value));

        _ = device.Submit(QueueType.Copy, [second]);
        GpuCompletion completion = device.Submit(QueueType.Copy, [first]);
        Assert.True(device.Wait(completion, TimeSpan.Zero));

        byte[] actual = new byte[16];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(expected, actual);
        Assert.Equal(2, device.Statistics.Submissions);
        Assert.Equal(2, device.Statistics.SubmittedCommandLists);
        Assert.Equal(2, device.Statistics.ExecutedCopies);

        device.DestroyBuffer(readback);
        device.DestroyBuffer(intermediate);
        device.DestroyBuffer(upload);
    }

    private static ShaderDesc Shader(ShaderStage stage, ulong identity) => new(
        ShaderKey(identity),
        ShaderBinaryFormat.SpirV,
        stage,
        stage switch
        {
            ShaderStage.Vertex => "VSMain",
            ShaderStage.Pixel => "PSMain",
            ShaderStage.Compute => "CSMain",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        },
        new byte[] { checked((byte)identity) },
        new ShaderInterface(
            Array.Empty<ShaderBinding>(),
            Array.Empty<PushConstantRange>(),
            identity));

    private static ShaderArtifactKey ShaderKey(ulong identity) => new(identity, 0, 0, 0);
}
