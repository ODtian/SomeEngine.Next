using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ShaderParameterTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Pairing_happens_once_before_execution_and_generates_binding_glue()
    {
        using NullDevice device = new();
        using GeneratedParameterFixture fixture = new(device);
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });

        GraphBuilder builder = graph.Begin();
        BufferId output = builder.ImportBuffer(
            fixture.Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("generated-shader-parameters", QueueSelection.Compute);
        GeneratedWriteShaderParameters parameters = new()
        {
            Output = new BufferParameter(
                output,
                new BufferRange(0, 16),
                BindingKind.StorageBuffer,
                BufferUse.ShaderWrite,
                ResourceEffect.Write,
                PriorContents: PriorContents.Discard,
                Coverage: WriteCoverage.Full),
            Value = new ConstantParameter<uint>(0xCAFE_BABE, 0),
        };
        GeneratedParameterSet bindings = parameters.Pair(ref builder, ref pass, fixture.Pairing);
        pass.UsesPipeline(fixture.Pipeline);
        GeneratedWriteShaderParameters frozen = parameters;
        PipelineHandle pipeline = fixture.Pipeline;
        pass.Execute((ICommandContext commands, in PassResources resources) =>
        {
            commands.SetPipeline(pipeline);
            frozen.Bind(bindings, commands, resources);
            commands.Dispatch(1, 1, 1);
        });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.True(execution.Wait(TimeSpan.Zero));
        Assert.Equal(1, device.Statistics.Dispatches);
        CapturePass captured = Assert.Single(execution.Capture!.Passes);
        Assert.Single(captured.Accesses);
        Assert.Equal("generated-shader-parameters", captured.Name);
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Cache_identity_and_asset_invalidation_follow_reflection_schema()
    {
        using NullDevice device = new();
        using GeneratedParameterFixture fixture = new(device);
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });

        Capture first = RecordContractOnly(graph, fixture, fixture.Pairing);
        Capture second = RecordContractOnly(graph, fixture, fixture.Pairing);
        Assert.Equal(first.ToJson(indented: false), second.ToJson(indented: false));
        Assert.Equal(1, graph.Statistics.CacheHits);

        ShaderDesc changed = GeneratedParameterFixture.ShaderDescription(layoutHash: 0xD404);
        ShaderParameterBinding changedPairing = new(
            changed,
            fixture.PipelineLayout,
            new[] { fixture.GroupLayout });
        Capture invalidated = RecordContractOnly(graph, fixture, changedPairing);

        Assert.NotEqual(first.CanonicalSignature, invalidated.CanonicalSignature);
        Assert.Equal(2, graph.Statistics.CacheMisses);
    }

    private static Capture RecordContractOnly(
        RenderGraph graph,
        GeneratedParameterFixture fixture,
        in ShaderParameterBinding pairing)
    {
        GraphBuilder builder = graph.Begin();
        BufferId output = builder.ImportBuffer(
            fixture.Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("generated-shader-cache", QueueSelection.Compute);
        GeneratedWriteShaderParameters parameters = new()
        {
            Output = new BufferParameter(
                output,
                new BufferRange(0, 16),
                BindingKind.StorageBuffer,
                BufferUse.ShaderWrite,
                ResourceEffect.Write,
                PriorContents: PriorContents.Discard,
                Coverage: WriteCoverage.Full),
            Value = new ConstantParameter<uint>(7, 0),
        };
        _ = parameters.Pair(ref builder, ref pass, pairing);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });
        return graph.Execute(ref builder).Capture!;
    }
}

[ShaderParameters]
internal partial struct GeneratedWriteShaderParameters
{
    public BufferParameter Output;
    public ConstantParameter<uint> Value;
}
