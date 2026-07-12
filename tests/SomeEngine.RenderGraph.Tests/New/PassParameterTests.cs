using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PassParameterTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Generated_access_view_constant_and_descriptor_glue_is_frozen_before_execute()
    {
        using NullDevice device = new();
        using GeneratedParameterFixture fixture = new(device);
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });

        GraphExecution execution = fixture.ExecutePassParameters(graph, new BufferRange(0, 16));

        Assert.True(execution.Wait(TimeSpan.Zero));
        Assert.Equal(1, device.Statistics.Dispatches);
        CapturePass pass = Assert.Single(execution.Capture!.Passes);
        Assert.Contains(pass.Accesses, static access =>
            access.Kind == "Buffer" && access.Effect == "Write" && access.Range == "0+16");
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Logical_resource_and_complete_view_form_one_atomic_value()
    {
        BufferParameter first = new(
            default,
            new BufferRange(0, 4),
            BindingKind.StorageBuffer,
            BufferUse.ShaderWrite,
            ResourceEffect.Write,
            Coverage: WriteCoverage.Full);
        BufferParameter second = first with { Range = new BufferRange(4, 4) };
        Assert.NotEqual(first, second);

        using NullDevice device = new();
        using GeneratedParameterFixture fixture = new(device);
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });
        GraphExecution execution = fixture.ExecutePassParameters(graph, second.Range);
        CaptureAccess access = Assert.Single(Assert.Single(execution.Capture!.Passes).Accesses);
        Assert.Equal("4+4", access.Range);
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Kind_count_access_and_foreign_field_errors_fail_closed()
    {
        using NullDevice device = new();
        using GeneratedParameterFixture fixture = new(device);
        using RenderGraph graph = new(device);

        GraphBuilder kindBuilder = graph.Begin();
        BufferId kindResource = kindBuilder.ImportBuffer(
            fixture.Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder kindPass = kindBuilder.AddPass("generated-kind-mismatch", QueueSelection.Compute);
        GeneratedWritePassParameters wrongKind = new()
        {
            Output = new BufferParameter(
                kindResource,
                new BufferRange(0, 16),
                BindingKind.ReadOnlyBuffer,
                BufferUse.ShaderRead,
                ResourceEffect.Read),
            Value = new ConstantParameter<uint>(1, 0),
        };
        Assert.IsType<InvalidOperationException>(CapturePairFailure(ref wrongKind, ref kindBuilder, ref kindPass, fixture.Pairing));
        kindBuilder.Dispose();

        GraphBuilder countBuilder = graph.Begin();
        BufferId countResource = countBuilder.ImportBuffer(
            fixture.Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder countPass = countBuilder.AddPass("generated-count-mismatch", QueueSelection.Compute);
        GeneratedWritePassParameters wrongCount = fixture.Parameters(countResource, new BufferRange(0, 16));
        ShaderDesc countShader = GeneratedParameterFixture.ShaderDescription(bindingCount: 2, layoutHash: 0xB202);
        ShaderParameterBinding countPairing = new(countShader, fixture.PipelineLayout, new[] { fixture.GroupLayout });
        Assert.IsType<InvalidOperationException>(CapturePairFailure(ref wrongCount, ref countBuilder, ref countPass, countPairing));
        countBuilder.Dispose();

        GraphBuilder accessBuilder = graph.Begin();
        BufferId accessResource = accessBuilder.ImportBuffer(
            fixture.Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder accessPass = accessBuilder.AddPass("generated-access-mismatch", QueueSelection.Compute);
        GeneratedWritePassParameters wrongAccess = fixture.Parameters(accessResource, new BufferRange(0, 16));
        ShaderDesc accessShader = GeneratedParameterFixture.ShaderDescription(
            reflectedAccess: ReflectedAccess.ReadWrite,
            declaredEffect: DeclaredEffect.ReadWrite,
            layoutHash: 0xB303);
        ShaderParameterBinding accessPairing = new(accessShader, fixture.PipelineLayout, new[] { fixture.GroupLayout });
        Assert.IsType<InvalidOperationException>(CapturePairFailure(ref wrongAccess, ref accessBuilder, ref accessPass, accessPairing));
        accessBuilder.Dispose();

        using RenderGraph foreignGraph = new(device);
        GraphBuilder foreignBuilder = foreignGraph.Begin();
        BufferId foreign = foreignBuilder.CreateBuffer(new BufferDesc(16, BufferUsage.ShaderWrite));
        GraphBuilder ownerBuilder = graph.Begin();
        PassBuilder ownerPass = ownerBuilder.AddPass("generated-foreign-field", QueueSelection.Compute);
        GeneratedWritePassParameters foreignParameters = fixture.Parameters(foreign, new BufferRange(0, 16));
        Assert.IsType<ArgumentException>(CapturePairFailure(ref foreignParameters, ref ownerBuilder, ref ownerPass, fixture.Pairing));
        ownerBuilder.Dispose();
        foreignBuilder.Dispose();
    }

    private static Exception CapturePairFailure(
        ref GeneratedWritePassParameters parameters,
        ref GraphBuilder builder,
        ref PassBuilder pass,
        in ShaderParameterBinding pairing)
    {
        try
        {
            parameters.Pair(ref builder, ref pass, pairing);
            return new Xunit.Sdk.XunitException("Generated pairing unexpectedly succeeded.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}

[PassParameters]
internal partial struct GeneratedWritePassParameters
{
    public BufferParameter Output;
    public ConstantParameter<uint> Value;
}

internal sealed class GeneratedParameterFixture : IDisposable
{
    private readonly NullDevice _device;
    private readonly ShaderHandle _shader;
    private readonly PipelineHandle _pipeline;

    public GeneratedParameterFixture(NullDevice device)
    {
        _device = device;
        Output = device.CreateBuffer(new BufferDesc(16, BufferUsage.ShaderRead | BufferUsage.ShaderWrite));
        GroupLayout = device.CreateBindGroupLayout(
            [new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute)]);
        PipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { GroupLayout },
            new[] { new PushConstantRange(0, 4, ShaderStage.Compute) }));
        Shader = ShaderDescription();
        Pairing = new ShaderParameterBinding(Shader, PipelineLayout, new[] { GroupLayout });
        _shader = device.CreateShader(Shader);
        _pipeline = device.CreateComputePipeline(new ComputePipelineDesc(PipelineLayout, _shader));
    }

    public BufferHandle Output { get; }
    public BindGroupLayoutHandle GroupLayout { get; }
    public PipelineLayoutHandle PipelineLayout { get; }
    public PipelineHandle Pipeline => _pipeline;
    public ShaderDesc Shader { get; }
    public ShaderParameterBinding Pairing { get; }

    public GeneratedWritePassParameters Parameters(BufferId output, BufferRange range) => new()
    {
        Output = new BufferParameter(
            output,
            range,
            BindingKind.StorageBuffer,
            BufferUse.ShaderWrite,
            ResourceEffect.Write,
            PriorContents: PriorContents.Discard,
            Coverage: WriteCoverage.Full),
        Value = new ConstantParameter<uint>(0x1234_5678, 0),
    };

    public GraphExecution ExecutePassParameters(RenderGraph graph, BufferRange range)
    {
        GraphBuilder builder = graph.Begin();
        BufferId output = builder.ImportBuffer(
            Output,
            BufferUse.ShaderWrite,
            BufferUse.ShaderWrite,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("generated-pass-parameters", QueueSelection.Compute);
        GeneratedWritePassParameters parameters = Parameters(output, range);
        GeneratedParameterSet bindings = parameters.Pair(ref builder, ref pass, Pairing);
        pass.UsesPipeline(_pipeline);
        GeneratedWritePassParameters frozen = parameters;
        PipelineHandle pipeline = _pipeline;
        pass.Execute((ICommandContext commands, in PassResources resources) =>
        {
            commands.SetPipeline(pipeline);
            frozen.Bind(bindings, commands, resources);
            commands.Dispatch(1, 1, 1);
        });
        return graph.Execute(ref builder);
    }

    public static ShaderDesc ShaderDescription(
        uint bindingCount = 1,
        ReflectedAccess reflectedAccess = ReflectedAccess.WriteOnly,
        DeclaredEffect declaredEffect = DeclaredEffect.Write,
        ulong layoutHash = 0xA101) => new(
        new ShaderArtifactKey(layoutHash, 2, 3, 4),
        ShaderBinaryFormat.Dxil,
        ShaderStage.Compute,
        "Main",
        new byte[] { 1 },
        new ShaderInterface(
            new ShaderBinding[] { new(
                0,
                0,
                BindingKind.StorageBuffer,
                bindingCount,
                ShaderStage.Compute,
                reflectedAccess,
                declaredEffect) },
            new PushConstantRange[] { new(0, 4, ShaderStage.Compute) },
            layoutHash));

    public void Dispose()
    {
        _device.DestroyPipeline(_pipeline);
        _device.DestroyShader(_shader);
        _device.DestroyPipelineLayout(PipelineLayout);
        _device.DestroyBindGroupLayout(GroupLayout);
        _device.DestroyBuffer(Output);
        _device.CollectGarbage();
    }
}
