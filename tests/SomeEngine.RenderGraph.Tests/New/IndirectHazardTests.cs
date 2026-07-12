using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class IndirectHazardTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Gpu_producer_to_draw_indirect_consumer_forms_exact_range_hazard() =>
        AssertExactHazard(IndirectKind.Draw, commandStride: DrawIndirectArguments.ByteSize, maxCommandCount: 2);

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Gpu_producer_to_indexed_indirect_consumer_forms_exact_range_hazard() =>
        AssertExactHazard(IndirectKind.DrawIndexed, commandStride: DrawIndexedIndirectArguments.ByteSize, maxCommandCount: 2);

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Gpu_producer_to_dispatch_indirect_consumer_forms_exact_range_hazard() =>
        AssertExactHazard(IndirectKind.Dispatch, commandStride: 16, maxCommandCount: 2);

    private static void AssertExactHazard(IndirectKind kind, uint commandStride, uint maxCommandCount)
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device);
        const ulong argumentOffset = 16;
        uint recordSize = kind switch
        {
            IndirectKind.Draw => DrawIndirectArguments.ByteSize,
            IndirectKind.DrawIndexed => DrawIndexedIndirectArguments.ByteSize,
            IndirectKind.Dispatch => DispatchIndirectArguments.ByteSize,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        ulong argumentBytes = checked((ulong)(maxCommandCount - 1) * commandStride + recordSize);
        BufferHandle sinkHandle = device.CreateBuffer(
            new BufferDesc(4, BufferUsage.CopyDestination),
            MemoryType.Readback);

        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId arguments = builder.CreateBuffer(new BufferDesc(
                160,
                BufferUsage.CopyDestination | BufferUsage.Indirect));
            BufferId sink = builder.ImportBuffer(
                sinkHandle,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);

            PassBuilder producer = builder.AddPass("indirect-overlapping-producer", QueueSelection.Copy);
            _ = producer.Write(
                arguments,
                BufferUse.CopyDestination,
                new BufferRange(argumentOffset, argumentBytes));
            producer.Execute(static (ICommandContext _, in PassResources _) => { });

            PassBuilder disjoint = builder.AddPass("indirect-disjoint-producer", QueueSelection.Copy);
            _ = disjoint.Write(
                arguments,
                BufferUse.CopyDestination,
                new BufferRange(128, 16));
            disjoint.Execute(static (ICommandContext _, in PassResources _) => { });

            QueueSelection queue = kind == IndirectKind.Dispatch ? QueueSelection.Compute : QueueSelection.Graphics;
            PassBuilder consumer = builder.AddPass("indirect-consumer", queue);
            BufferAccess access = consumer.Read(
                arguments,
                BufferUse.Indirect,
                new BufferRange(argumentOffset, argumentBytes));
            _ = consumer.Write(sink, BufferUse.CopyDestination, new BufferRange(0, 4));
            consumer.Execute((ICommandContext commands, in PassResources resources) =>
            {
                BufferHandle handle = resources.Get(access);
                switch (kind)
                {
                    case IndirectKind.Draw:
                        commands.DrawIndirect(handle, argumentOffset, maxCommandCount, commandStride);
                        break;
                    case IndirectKind.DrawIndexed:
                        commands.DrawIndexedIndirect(handle, argumentOffset, maxCommandCount, commandStride);
                        break;
                    case IndirectKind.Dispatch:
                        commands.DispatchIndirect(handle, argumentOffset, maxCommandCount, commandStride);
                        break;
                }
            });

            GraphRecording recording = builder.Consume(graph);
            graph.Abandon(recording);
            FrozenGraph frozen = recording.Freeze(device);
            CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: false);

            Assert.Contains(0, compiled.Dependencies[2]);
            Assert.DoesNotContain(1, compiled.Dependencies[2]);
            FrozenAccess declared = Assert.Single(
                frozen.Passes[2].Accesses,
                static value => value.Kind == ResourceNodeKind.Buffer && value.BufferUse == BufferUse.Indirect);
            Assert.Equal(new BufferRange(argumentOffset, argumentBytes), declared.BufferRange);

            using IndirectPipelineResources pipelines = new(device);
            Assert.Null(ExecuteEnvelope(
                device,
                pipelines,
                kind,
                argumentOffset,
                argumentBytes,
                new BufferRange(argumentOffset, argumentBytes),
                maxCommandCount,
                commandStride));
            AssertOutsideEnvelope(ExecuteEnvelope(
                device,
                pipelines,
                kind,
                argumentOffset,
                argumentBytes,
                new BufferRange(argumentOffset, argumentBytes - 1),
                maxCommandCount,
                commandStride));
            AssertOutsideEnvelope(ExecuteEnvelope(
                device,
                pipelines,
                kind,
                argumentOffset,
                argumentBytes,
                new BufferRange(argumentOffset + 1, argumentBytes - 1),
                maxCommandCount,
                commandStride));
        }
        finally
        {
            device.DestroyBuffer(sinkHandle);
        }
    }

    private static Exception? ExecuteEnvelope(
        NullDevice device,
        IndirectPipelineResources pipelines,
        IndirectKind kind,
        ulong argumentOffset,
        ulong argumentBytes,
        BufferRange declaredRange,
        uint maxCommandCount,
        uint commandStride)
    {
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });
        BufferHandle observable = device.CreateBuffer(
            new BufferDesc(4, BufferUsage.CopyDestination),
            MemoryType.Readback);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId arguments = builder.CreateBuffer(new BufferDesc(
                checked(argumentOffset + argumentBytes),
                BufferUsage.CopyDestination | BufferUsage.Indirect));
            BufferId index = default;
            PassBuilder producer = builder.AddPass("indirect-envelope-producer", QueueSelection.Copy);
            _ = producer.Write(
                arguments,
                BufferUse.CopyDestination,
                new BufferRange(argumentOffset, argumentBytes));
            if (kind == IndirectKind.DrawIndexed)
            {
                index = builder.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination | BufferUsage.Index));
                _ = producer.Write(index, BufferUse.CopyDestination);
            }
            producer.Execute(static (ICommandContext _, in PassResources _) => { });

            PassBuilder consumer = builder.AddPass(
                "indirect-envelope-consumer",
                kind == IndirectKind.Dispatch ? QueueSelection.Compute : QueueSelection.Graphics);
            BufferAccess argumentAccess = consumer.Read(arguments, BufferUse.Indirect, declaredRange);
            BufferAccess indexAccess = default;
            if (kind == IndirectKind.DrawIndexed)
                indexAccess = consumer.Read(index, BufferUse.Index);

            if (kind == IndirectKind.Dispatch)
            {
                BufferId output = builder.ImportBuffer(
                    observable,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);
                _ = consumer.Write(output, BufferUse.CopyDestination);
                consumer.UsesShader(pipelines.ComputeDescription);
                consumer.UsesPipeline(pipelines.ComputePipeline);
            }
            else
            {
                TextureId color = builder.CreateTexture(new TextureDesc(
                    1,
                    1,
                    Format.R8G8B8A8UNorm,
                    TextureUsage.ColorAttachment | TextureUsage.CopySource));
                TextureViewId colorView = builder.CreateTextureView(
                    color,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                    TextureViewUsage.ColorAttachment);
                _ = consumer.ColorAttachment(0, colorView, LoadAction.Clear, Vector4.Zero);
                consumer.UsesShader(pipelines.VertexDescription);
                consumer.UsesShader(pipelines.PixelDescription);
                consumer.UsesPipeline(pipelines.RasterPipeline);

                BufferId output = builder.ImportBuffer(
                    observable,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);
                PassBuilder publish = builder.AddPass("indirect-envelope-publish", QueueSelection.Copy);
                _ = publish.Read(color, TextureUse.CopySource);
                _ = publish.Write(output, BufferUse.CopyDestination);
                publish.Execute(static (ICommandContext _, in PassResources _) => { });
            }

            consumer.Execute((ICommandContext commands, in PassResources resources) =>
            {
                BufferHandle argumentHandle = resources.Get(argumentAccess);
                if (kind == IndirectKind.Dispatch)
                {
                    commands.SetPipeline(pipelines.ComputePipeline);
                    commands.DispatchIndirect(argumentHandle, argumentOffset, maxCommandCount, commandStride);
                    return;
                }

                commands.SetPipeline(pipelines.RasterPipeline);
                if (kind == IndirectKind.DrawIndexed)
                {
                    commands.SetIndexBuffer(resources.Get(indexAccess), 0, IndexFormat.UInt16);
                    commands.DrawIndexedIndirect(argumentHandle, argumentOffset, maxCommandCount, commandStride);
                }
                else
                {
                    commands.DrawIndirect(argumentHandle, argumentOffset, maxCommandCount, commandStride);
                }
            });

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.Zero));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            device.DestroyBuffer(observable);
            device.CollectGarbage();
        }
    }

    private static void AssertOutsideEnvelope(Exception? error)
    {
        Assert.NotNull(error);
        Assert.Contains("outside this pass's declared", error.ToString(), StringComparison.Ordinal);
    }

    private enum IndirectKind : byte
    {
        Draw,
        DrawIndexed,
        Dispatch,
    }

    private sealed class IndirectPipelineResources : IDisposable
    {
        private readonly NullDevice _device;

        public IndirectPipelineResources(NullDevice device)
        {
            _device = device;
            PipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>()));
            ComputeDescription = Shader(ShaderStage.Compute, 0x101);
            VertexDescription = Shader(ShaderStage.Vertex, 0x201);
            PixelDescription = Shader(ShaderStage.Pixel, 0x301);
            ComputeShader = device.CreateShader(ComputeDescription);
            VertexShader = device.CreateShader(VertexDescription);
            PixelShader = device.CreateShader(PixelDescription);
            ComputePipeline = device.CreateComputePipeline(new ComputePipelineDesc(PipelineLayout, ComputeShader));
            RasterPipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
                PipelineLayout,
                VertexShader,
                PixelShader,
                new[] { Format.R8G8B8A8UNorm },
                Rasterizer: new RasterizerDesc(FillMode.Solid, CullMode.None, FrontFace.CounterClockwise, DepthClip: true),
                DepthStencil: new DepthStencilDesc(false, false, CompareOp.Always),
                BlendAttachments: new[]
                {
                    new BlendAttachmentDesc(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        ColorWriteMask.All),
                }));
        }

        public ShaderDesc ComputeDescription { get; }
        public ShaderDesc VertexDescription { get; }
        public ShaderDesc PixelDescription { get; }
        public ShaderHandle ComputeShader { get; }
        public ShaderHandle VertexShader { get; }
        public ShaderHandle PixelShader { get; }
        public PipelineLayoutHandle PipelineLayout { get; }
        public PipelineHandle ComputePipeline { get; }
        public PipelineHandle RasterPipeline { get; }

        public void Dispose()
        {
            _device.DestroyPipeline(RasterPipeline);
            _device.DestroyPipeline(ComputePipeline);
            _device.DestroyShader(PixelShader);
            _device.DestroyShader(VertexShader);
            _device.DestroyShader(ComputeShader);
            _device.DestroyPipelineLayout(PipelineLayout);
            _device.CollectGarbage();
        }

        private static ShaderDesc Shader(ShaderStage stage, ulong seed) => new(
            new ShaderArtifactKey(seed, seed + 1, seed + 2, seed + 3),
            ShaderBinaryFormat.Dxil,
            stage,
            "Main",
            new byte[] { 1 },
            new ShaderInterface(
                Array.Empty<ShaderBinding>(),
                Array.Empty<PushConstantRange>(),
                seed + 4));
    }
}
