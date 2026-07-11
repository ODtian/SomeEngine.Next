using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CommandCapabilityTests
{
    [Fact]
    public void Copy_rejects_a_captured_buffer_that_is_not_part_of_the_graph_invocation()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        BufferHandle captured = device.CreateBuffer(
            new BufferDesc(64, BufferUsage.CopySource),
            MemoryType.Upload);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId destination = builder.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
            PassBuilder pass = builder.AddPass("captured-buffer", QueueSelection.Copy);
            output.Root(ref builder, ref pass);
            BufferAccess destinationAccess = pass.Write(destination, BufferUse.CopyDestination);
            pass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(captured, 0, resources.Get(destinationAccess), 0, 16));

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            Assert.Contains("not declared by this render-graph invocation", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(captured);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Copy_rejects_a_range_outside_the_pass_declared_access()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        BufferDesc sourceDesc = new(64, BufferUsage.CopySource);
        BufferHandle sourceHandle = device.CreateBuffer(sourceDesc, MemoryType.Upload);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(
                sourceHandle,
                BufferUse.CopySource,
                BufferUse.CopySource);
            BufferId destination = builder.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
            PassBuilder pass = builder.AddPass("range-envelope", QueueSelection.Copy);
            output.Root(ref builder, ref pass);
            BufferAccess sourceAccess = pass.Read(source, BufferUse.CopySource, new BufferRange(0, 16));
            BufferAccess destinationAccess = pass.Write(
                destination,
                BufferUse.CopyDestination,
                new BufferRange(0, 16));
            pass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(
                    resources.Get(sourceAccess),
                    sourceOffset: 8,
                    resources.Get(destinationAccess),
                    destinationOffset: 0,
                    size: 16));

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            Assert.Contains("outside this pass's declared Read/CopySource access range", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(sourceHandle);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void SetPipeline_rejects_a_pipeline_not_declared_by_UsesPipeline()
    {
        using NullDevice device = new(new NullOptions());
        using ComputePipelineResources compute = new(device);
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass("undeclared-pipeline", QueueSelection.Compute);
        output.Root(ref builder, ref pass);
        pass.Execute((ICommandContext commands, in PassResources _) => commands.SetPipeline(compute.Pipeline));

        InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

        Assert.Contains("not frozen as an allowed choice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsesPipeline_allows_the_exact_declared_pipeline()
    {
        using NullDevice device = new(new NullOptions());
        using ComputePipelineResources compute = new(device);
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass("declared-pipeline", QueueSelection.Compute);
        output.Root(ref builder, ref pass);
        pass.UsesShader(compute.ShaderDescription);
        pass.UsesPipeline(compute.Pipeline);
        pass.Execute((ICommandContext commands, in PassResources _) => commands.SetPipeline(compute.Pipeline));

        GraphExecution execution = graph.Execute(ref builder);

        Assert.True(execution.Wait(TimeSpan.Zero));
        Assert.Single(execution.Completions);
    }

    [Fact]
    public void UsesPipeline_rejects_shader_contracts_that_do_not_match_the_live_pipeline()
    {
        using NullDevice device = new(new NullOptions());
        using ComputePipelineResources compute = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass("pipeline-shader-mismatch", QueueSelection.Compute);
        pass.UsesShader(ZeroBindingShader(new ShaderArtifactKey(0xA1, 0xB2, 0xC3, 0xD4)));
        pass.UsesPipeline(compute.Pipeline);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

        Assert.Contains("absent from its frozen UsesShader contracts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetBindings_rejects_a_different_declared_view_than_the_exact_shader_mapping()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferViewId mappedView = CreateStorageView(ref builder, "mapped");
        BufferViewId otherView = CreateStorageView(ref builder, "other");
        PassBuilder pass = builder.AddPass("exact-view", QueueSelection.Compute);
        output.Root(ref builder, ref pass);
        BufferViewAccess mappedAccess = pass.Write(mappedView);
        BufferViewAccess otherAccess = pass.Write(otherView);
        ShaderBindingAccess mapping = pass.MapShaderBinding(0, 0, mappedAccess);
        ShaderDesc shader = StorageWriteShader();
        ShaderBindingAccess[] mappings = [mapping];
        pass.UsesShader(shader, mappings);
        pass.Execute((ICommandContext commands, in PassResources resources) =>
        {
            BindingWrite[] writes = [BindingWrite.Buffer(0, resources.Get(otherAccess))];
            commands.SetBindings(0, default, writes);
        });

        InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

        Assert.Contains("does not match the pass's exact shader mapping", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opaque_SetBindGroup_is_rejected_even_for_a_valid_device_group()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        BindGroupLayoutHandle layout = device.CreateBindGroupLayout([]);
        BindGroupHandle group = device.CreateBindGroup(layout, []);
        try
        {
            using RenderGraph graph = new(device);
            GraphBuilder builder = graph.Begin();
            PassBuilder pass = builder.AddPass("opaque-bind-group", QueueSelection.Compute);
            output.Root(ref builder, ref pass);
            pass.Execute((ICommandContext commands, in PassResources _) => commands.SetBindGroup(0, group));

            NotSupportedException error = ExecuteExpecting<NotSupportedException>(graph, ref builder);

            Assert.Contains("Opaque bind groups cannot be verified", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBindGroup(group);
            device.DestroyBindGroupLayout(layout);
            device.CollectGarbage();
        }
    }

    private static BufferViewId CreateStorageView(ref GraphBuilder builder, string name)
    {
        BufferId buffer = builder.CreateBuffer(new BufferDesc(64, BufferUsage.ShaderWrite, name));
        return builder.CreateBufferView(
            buffer,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            stride: 16,
            name: $"{name}-view");
    }

    private static ShaderDesc StorageWriteShader() => new(
        new ShaderArtifactKey(0x11, 0x22, 0x33, 0x44),
        ShaderBinaryFormat.Dxil,
        ShaderStage.Compute,
        "Main",
        new byte[] { 1, 2, 3, 4 },
        new ShaderInterface(
            new[] { new ShaderBinding(
                0,
                0,
                BindingKind.StorageBuffer,
                1,
                ShaderStage.Compute,
                ReflectedAccess.WriteOnly,
                DeclaredEffect.Write) },
            Array.Empty<PushConstantRange>(),
            0x5566_7788_99AA_BBCCUL),
        "capability-test");

    private static ShaderDesc ZeroBindingShader(ShaderArtifactKey key) => new(
        key,
        ShaderBinaryFormat.Dxil,
        ShaderStage.Compute,
        "Main",
        new byte[] { 1 },
        new ShaderInterface(
            Array.Empty<ShaderBinding>(),
            Array.Empty<PushConstantRange>(),
            0xABCD),
        "zero-binding-capability-test");

    private static TException ExecuteExpecting<TException>(RenderGraph graph, ref GraphBuilder builder)
        where TException : Exception
    {
        try
        {
            _ = graph.Execute(ref builder);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException($"Expected {typeof(TException).Name}.");
    }

    private sealed class ObservableOutput : IDisposable
    {
        private readonly NullDevice _device;
        private readonly BufferHandle _buffer;

        public ObservableOutput(NullDevice device)
        {
            _device = device;
            _buffer = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination));
        }

        public void Root(ref GraphBuilder builder, ref PassBuilder pass)
        {
            BufferId output = builder.ImportBuffer(
                _buffer,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            _ = pass.Write(output, BufferUse.CopyDestination);
        }

        public void Dispose()
        {
            _device.DestroyBuffer(_buffer);
            _device.CollectGarbage();
        }
    }

    private sealed class ComputePipelineResources : IDisposable
    {
        private readonly NullDevice _device;

        public ComputePipelineResources(NullDevice device)
        {
            _device = device;
            ShaderDescription = new ShaderDesc(
                new ShaderArtifactKey(0x101, 0x202, 0x303, 0x404),
                ShaderBinaryFormat.Dxil,
                ShaderStage.Compute,
                "Main",
                new byte[] { 1 },
                new ShaderInterface(
                    Array.Empty<ShaderBinding>(),
                    Array.Empty<PushConstantRange>(),
                    0x505),
                "pipeline-capability-test");
            Shader = device.CreateShader(ShaderDescription);
            Layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>(),
                "pipeline-capability-layout"));
            Pipeline = device.CreateComputePipeline(new ComputePipelineDesc(Layout, Shader, "pipeline-capability"));
        }

        public ShaderHandle Shader { get; }
        public ShaderDesc ShaderDescription { get; }
        public PipelineLayoutHandle Layout { get; }
        public PipelineHandle Pipeline { get; }

        public void Dispose()
        {
            _device.DestroyPipeline(Pipeline);
            _device.DestroyShader(Shader);
            _device.DestroyPipelineLayout(Layout);
            _device.CollectGarbage();
        }
    }
}
