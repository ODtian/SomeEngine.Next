using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class PersistentBindingReuseTests
{
    [Fact]
    public void PersistentBindingsRemainCorrectAfterPartialTransientReplacement()
    {
        using D3D12TestShaderProgram pairShader = D3D12TestShaderProgram.Compile("binding_pair", """
            RWBuffer<uint> first;
            RWBuffer<uint> second;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { first[0] = 11; second[0] = 22; }
            """, [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)], "sm_6_2");
        using D3D12TestShaderProgram singleShader = D3D12TestShaderProgram.Compile("binding_single", """
            RWBuffer<uint> value;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { value[0] = 99; }
            """, [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)], "sm_6_2");
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pair = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(pairShader.Program, pairShader.GetEntryPoint(0)));
        using Pipeline single = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(singleShader.Program, singleShader.GetEntryPoint(0)));
        ShaderVariableLayout pairLayout = pairShader.Reflection.GetGlobalParamsVarLayout()!;
        ShaderVariableLayout singleLayout = singleShader.Reflection.GetGlobalParamsVarLayout()!;
        var description = new BufferDesc(4,
            BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination);
        using Buffer first = backend.CreateBuffer(device, description);
        using Buffer second = backend.CreateBuffer(device, description);
        using Buffer alternate = backend.CreateBuffer(device, description);
        using BufferUav firstView = backend.CreateBufferUav(device, new BufferUavDesc(first, BufferRange.Whole, Format.R32UInt));
        using BufferUav secondView = backend.CreateBufferUav(device, new BufferUavDesc(second, BufferRange.Whole, Format.R32UInt));
        using BufferUav alternateView = backend.CreateBufferUav(device, new BufferUavDesc(alternate, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(device, pair,
            new ParameterBlockBindings(pairLayout,
                [ResourceBinding.WritableBuffer(firstView), ResourceBinding.WritableBuffer(secondView)], []));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, InitialSlotCount: 1));

        // Reuse the same context across recordings with different parameter layouts.
        backend.Begin(context, new CommandRecordingDesc(4, 0, 8));
        backend.SetPipeline(context, pair);
        backend.SetPersistentParameterBindings(context, bindings);
        backend.Discard(context);
        backend.Begin(context, new CommandRecordingDesc(4, 0, 8));
        backend.SetPipeline(context, single);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(singleLayout, [ResourceBinding.WritableBuffer(firstView)], []));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(singleLayout, [ResourceBinding.WritableBuffer(alternateView)], []));
        backend.Discard(context);

        backend.Begin(context, new CommandRecordingDesc(4, 0, 8));
        foreach (Buffer buffer in new[] { first, second, alternate })
        {
            backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            backend.ClearBuffer(context, buffer, BufferRange.Whole, 0);
            backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        backend.SetPipeline(context, pair);
        backend.SetPersistentParameterBindings(context, bindings);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        foreach (Buffer buffer in new[] { first, second })
            backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(first, 0, readback, 0, 4));
        backend.CopyBuffer(context, new BufferCopy(second, 0, readback, 4, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal(new uint[] { 11, 22 }, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
        backend.CollectCompleted(device);
    }
}
