using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class QueryHazardTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Query_resolve_destination_is_an_exact_graph_write() =>
        AssertExactResolve(QueryType.Timestamp);

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Occlusion_resolve_is_declared_and_cannot_escape_the_pass()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device);
        QueryPoolHandle pool = device.CreateQueryPool(new QueryPoolDesc(QueryType.Occlusion, 2));
        BufferHandle destination = device.CreateBuffer(
            new BufferDesc(32, BufferUsage.CopyDestination),
            MemoryType.Readback);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId result = builder.ImportBuffer(
                destination,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass("occlusion-resolve-escape", QueueSelection.Compute);
            const ulong exactResolveSize = 16 + sizeof(ulong);
            _ = pass.Write(result, BufferUse.CopyDestination, new BufferRange(8, exactResolveSize - 1));
            pass.UsesQueryPool(pool);
            pass.Execute((ICommandContext commands, in PassResources _) =>
            {
                commands.ResetQueryPool(pool, 0, 2);
                commands.ResolveQueryPool(pool, 0, 2, destination, 8, destinationStride: 16);
            });

            Exception error = CaptureFailure(graph, ref builder);
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("outside this pass's declared", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(destination);
            device.DestroyQueryPool(pool);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Pipeline_statistics_resolve_is_an_exact_graph_write() =>
        AssertExactResolve(QueryType.PipelineStatistics);

    [Fact]
    public void Query_pool_must_be_frozen_for_the_executing_pass()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device);
        QueryPoolHandle allowed = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 1));
        QueryPoolHandle escaped = device.CreateQueryPool(new QueryPoolDesc(QueryType.Timestamp, 1));
        BufferHandle output = device.CreateBuffer(
            new BufferDesc(8, BufferUsage.CopyDestination),
            MemoryType.Readback);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId observable = builder.ImportBuffer(
                output,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass("query-pool-envelope", QueueSelection.Compute);
            _ = pass.Write(observable, BufferUse.CopyDestination);
            pass.UsesQueryPool(allowed);
            pass.Execute((ICommandContext commands, in PassResources _) =>
                commands.ResetQueryPool(escaped, 0, 1));

            Exception error = CaptureFailure(graph, ref builder);
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("not frozen", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyQueryPool(escaped);
            device.DestroyQueryPool(allowed);
        }
    }

    private static void AssertExactResolve(QueryType type)
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });
        const uint queryCount = 2;
        QueryPoolDesc queryDesc = new(type, queryCount);
        QueryPoolHandle pool = device.CreateQueryPool(queryDesc);
        ShaderHandle shader = default;
        PipelineLayoutHandle pipelineLayout = default;
        PipelineHandle pipeline = default;
        ShaderDesc shaderDescription = default;
        if (type == QueryType.PipelineStatistics)
        {
            shaderDescription = new ShaderDesc(
                new ShaderArtifactKey(0x91, 0x92, 0x93, 0x94),
                ShaderBinaryFormat.Dxil,
                ShaderStage.Compute,
                "Main",
                new byte[] { 1 },
                new ShaderInterface(
                    Array.Empty<ShaderBinding>(),
                    Array.Empty<PushConstantRange>(),
                    0x95));
            shader = device.CreateShader(shaderDescription);
            pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>()));
            pipeline = device.CreateComputePipeline(new ComputePipelineDesc(pipelineLayout, shader));
        }
        const ulong destinationOffset = 8;
        ulong resultSize = queryDesc.ResultSize;
        ulong resultStride = checked(resultSize + 8);
        ulong exactResolveSize = checked((queryCount - 1) * resultStride + resultSize);
        BufferHandle destination = device.CreateBuffer(
            new BufferDesc(destinationOffset + exactResolveSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId result = builder.ImportBuffer(
                destination,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass($"{type}-resolve", QueueSelection.Compute);
            _ = pass.Write(
                result,
                BufferUse.CopyDestination,
                new BufferRange(destinationOffset, exactResolveSize));
            pass.UsesQueryPool(pool);
            if (type == QueryType.PipelineStatistics)
            {
                pass.UsesShader(shaderDescription);
                pass.UsesPipeline(pipeline);
            }
            pass.Execute((ICommandContext commands, in PassResources _) =>
            {
                commands.ResetQueryPool(pool, 0, queryCount);
                if (type == QueryType.Timestamp)
                {
                    commands.WriteTimestamp(pool, 0);
                    commands.WriteTimestamp(pool, 1);
                }
                if (type == QueryType.PipelineStatistics)
                {
                    commands.SetPipeline(pipeline);
                    for (uint index = 0; index < queryCount; index++)
                    {
                        commands.BeginQuery(pool, index);
                        commands.EndQuery(pool, index);
                    }
                }
                commands.ResolveQueryPool(pool, 0, queryCount, destination, destinationOffset, resultStride);
            });

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.Zero));
            CaptureAccess access = Assert.Single(Assert.Single(execution.Capture!.Passes).Accesses);
            Assert.Equal(FormattableString.Invariant($"{destinationOffset}+{exactResolveSize}"), access.Range);
            Assert.Equal(nameof(BufferUse.CopyDestination), access.Use);
        }
        finally
        {
            if (pipeline.IsValid) device.DestroyPipeline(pipeline);
            if (pipelineLayout.IsValid) device.DestroyPipelineLayout(pipelineLayout);
            if (shader.IsValid) device.DestroyShader(shader);
            device.DestroyBuffer(destination);
            device.DestroyQueryPool(pool);
        }
    }

    private static Exception CaptureFailure(RenderGraph graph, ref GraphBuilder builder)
    {
        try
        {
            _ = graph.Execute(ref builder);
            return new Xunit.Sdk.XunitException("Query resolve unexpectedly escaped its declared range.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
