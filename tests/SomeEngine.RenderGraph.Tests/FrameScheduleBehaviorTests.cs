using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Resources;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameScheduleBehaviorTests
{
    [Fact]
    public void PersistentSchedulingIsSharedAcrossThreeInFlightFrames()
    {
        using var support = new ScheduleDevice();
        using ExternalTimeline gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        _ = support.Backend.Submit(support.Graphics, new([], [new(gate, 1)], [], [], []));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics, support.Copy],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        var result = new ReadbackValues();
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddCopyFramePass("clear shared buffer", PassQueueSelection.OnQueue(support.Graphics), buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId data) => access.Write(data, new(0, 4),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphBufferId data) => commands.ClearBuffer(commands.GetBuffer(data), new(0, 4), 42));
            readback = edit.AddReadback(buffer, new(0, 4), result, "read shared buffer");
            edit.Commit();
        }
        var identities = new ulong[3];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                RenderGraphStatistics statistics = default;
                using RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
                frame.SetReadbackTag(readback, (ulong)index);
                identities[index] = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
                if (index == 0) Assert.True(statistics.AnalyzedPassCount > 0);
                else
                {
                    Assert.True(statistics.AnalysisReused);
                    Assert.Equal(0, statistics.AnalyzedPassCount);
                }
            }
        }
        finally { support.Backend.Submit(support.Compute, new([], [], [], [], [new(gate, 1)])); }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identities[^1], TimeSpan.FromSeconds(10)));
        Assert.Equal(new uint[] { 42, 42, 42 }, result.Values.OrderBy(value => value.Tag).Select(value => value.Value));
    }

    [Fact]
    public void DiscardedFramesKeepFeatureStateAndGpuDataAtTheLastSubmission()
    {
        using var support = new ScheduleDevice();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        var result = new ReadbackValues();
        var counter = new SubmissionCounter();
        GraphBufferId buffer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(buffer);
            edit.AddReadback(buffer, new(0, 4), result, "read submitted counter");
            edit.Commit();
        }
        ulong last = 0;
        for (int index = 0; index < 3; index++)
        {
            Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(10)));
            using var preparation = next;
            SubmittedCounter data = next.GetFrameData(counter, () => new SubmittedCounter(counter, counter.Value + 1));
            using RenderGraphFrame frame = graph.BeginFrame(next);
            Assert.Same(data, frame.GetFrameData(counter, () => new SubmittedCounter(counter, counter.Value + 1)));
            frame.WriteBuffer(buffer, 0, BitConverter.GetBytes(data.Value));
            frame.ObserveExecution(data);
            frame.ObserveExecution(data);
            if (index == 1) continue;
            last = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(last, TimeSpan.FromSeconds(10)));
        Assert.Equal(2u, counter.Value);
        Assert.Equal(new uint[] { 1, 2 }, result.Values.Select(value => value.Value));
    }

    [Fact]
    public void CallbackFailureMakesTheGraphLost()
    {
        using var support = new ScheduleDevice();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("fail frame recording", PassQueueSelection.OnQueue(support.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => throw new FormatException());
            edit.Commit();
        }
        Exception? failure = null;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            try { frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]); }
            catch (Exception error) { failure = error; }
        }
        Assert.IsType<FormatException>(failure);
        Assert.True(graph.IsLost);
        Assert.Throws<RenderGraphLostException>(() => graph.TryBeginFrame(out _));
    }

    [Fact]
    public async Task ChangingFrameSectionsKeepThePersistentScheduleAndFeedItsReader()
    {
        using var support = new ScheduleDevice();
        using var shaders = new ShaderLibrary(SlangCompileTarget.Dxil);
        Shader shader = await shaders.CreateAsync("frame section writer", "frame_section_writer.slang", """
            StructuredBuffer<uint> Values;
            RWStructuredBuffer<uint> Data;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Data[0] = Values[0]; }
            """);
        EntryPointReflection entry = shader.RequireSlangEntry(0, "Main", SlangStage.Compute);
        using Pipeline pipeline = support.Backend.CreateComputePipeline(support.Device,
            new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0), entry,
                shader.GetSlangEntryBytes(0, entry), CodeBlob: shader.GetSlangEntryBlob(0, entry)));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics, support.Compute, support.Copy]);
        var result = new ReadbackValues();
        GraphBufferId data, observed;
        GraphRegionId region;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            region = edit.AddRegion("persistent reader");
            data = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            observed = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddCopyFramePass("read section data", PassQueueSelection.OnQueue(support.Graphics), (data, observed),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref (GraphBufferId Data, GraphBufferId Observed) state) =>
                {
                    access.Read(state.Data, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    access.Write(state.Observed, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Data, GraphBufferId Observed) state) =>
                    commands.CopyBuffer(new(commands.GetBuffer(state.Data), 0, commands.GetBuffer(state.Observed), 0, 4)), region);
            edit.AddReadback(observed, new(0, 4), result, "read persistent result", region);
            edit.Commit();
        }
        for (int index = 0; index < 3; index++)
        {
            RenderGraphStatistics statistics = default;
            ulong identity;
            using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics)))
            {
                for (int item = 0; item <= index; item++)
                {
                    GraphBufferSlice value = frame.Upload(BitConverter.GetBytes((uint)(10 + item)), BufferUsages.ShaderRead);
                    GraphBufferSrvId input = frame.CreateBufferSrv(value, structureStride: 4);
                    GraphBufferUavId output = frame.CreateBufferUav(data, structureStride: 4);
                    frame.AddComputePass("write frame section", PassQueueSelection.OnQueue(support.Compute), 0,
                        new PassOptions(Culling: PassCullingMode.NeverCull), pipeline,
                        shader.GetSlangReflection(0).GetGlobalParamsVarLayout()!.Value,
                        [GraphParameterResourceBinding.ReadOnlyBuffer(input, PipelineSync.ComputeShading),
                         GraphParameterResourceBinding.WritableBuffer(output, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete)],
                        static (ref PassDefinition _, ref int _) => { },
                        static (ref ComputePassCommandScope commands, in int _) => commands.Dispatch(new(1, 1, 1)),
                        region, FrameSectionPosition.Start);
                }
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            Assert.Equal(index + 1, statistics.FrameSectionPassCount);
            if (index != 0) Assert.True(statistics.PersistentScheduleReused);
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
            Assert.Equal((uint)(10 + index), result.Values[^1].Value);
        }
    }

    [Fact]
    public async Task RetainedShaderInputsAdvanceOnlyWhenTheirFrameIsSubmitted()
    {
        using var support = new ScheduleDevice();
        using var shaders = new ShaderLibrary(SlangCompileTarget.Dxil);
        Shader shader = await shaders.CreateAsync("retained input copy", "retained_input_copy.slang", """
            StructuredBuffer<uint> Values;
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Result[0] = Values[0]; }
            """);
        EntryPointReflection entry = shader.RequireSlangEntry(0, "Main", SlangStage.Compute);
        using Pipeline pipeline = support.Backend.CreateComputePipeline(support.Device,
            new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0), entry,
                shader.GetSlangEntryBytes(0, entry), CodeBlob: shader.GetSlangEntryBlob(0, entry)));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        GraphBufferId first, second;
        GraphParameterInput input;
        var result = new ReadbackValues();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderRead | BufferUsages.CopyDestination));
            second = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderRead | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(first);
            edit.AddBufferUploadPass(second);
            GraphBufferId output = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            GraphBufferUavId view = edit.CreateBufferUav(output, structureStride: 4);
            input = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            edit.AddComputeFramePass("copy retained input", PassQueueSelection.OnQueue(support.Graphics),
                (Input: input, Output: view), new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline, shader.GetSlangReflection(0).GetGlobalParamsVarLayout()!.Value, [],
                static (ref PassDefinition access, ref (GraphParameterInput Input, GraphBufferUavId Output) data) =>
                {
                    access.Bind(data.Input, PipelineSync.ComputeShading);
                    access.Bind(data.Output, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                },
                static (ref ComputePassCommandScope commands, in (GraphParameterInput Input, GraphBufferUavId Output) _) =>
                    commands.Dispatch(new(1, 1, 1)));
            edit.AddReadback(output, new(0, 4), result, "read retained input");
            edit.Commit();
        }
        using BufferSrv firstView = support.Backend.CreateBufferSrv(support.Device,
            new(graph.GetBuffer(first), new(0, 4), StructureStride: 4));
        using BufferSrv secondView = support.Backend.CreateBufferSrv(support.Device,
            new(graph.GetBuffer(second), new(0, 4), StructureStride: 4));
        ulong identity;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(first, 0, BitConverter.GetBytes(1u));
            frame.WriteBuffer(second, 0, BitConverter.GetBytes(2u));
            frame.SetPersistentParameterInput(input, ResourceBinding.ReadOnlyBuffer(firstView));
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        using (RenderGraphFrame canceled = graph.BeginFrame())
            canceled.SetPersistentParameterInput(input, ResourceBinding.ReadOnlyBuffer(secondView));
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(new uint[] { 1, 1 }, result.Values.Select(value => value.Value));
    }

    [Fact]
    public async Task DisabledPassInputsAreSuppliedOnlyWhenTheirPassRuns()
    {
        using var support = new ScheduleDevice();
        using var shaders = new ShaderLibrary(SlangCompileTarget.Dxil);
        Shader shader = await shaders.CreateAsync("optional input copy", "optional_input_copy.slang", """
            StructuredBuffer<uint> Values;
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Result[0] = Values[0]; }
            """);
        EntryPointReflection entry = shader.RequireSlangEntry(0, "Main", SlangStage.Compute);
        using Pipeline pipeline = support.Backend.CreateComputePipeline(support.Device,
            new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0), entry,
                shader.GetSlangEntryBytes(0, entry), CodeBlob: shader.GetSlangEntryBlob(0, entry)));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        GraphBufferInput input;
        GraphBufferId output, first, second;
        GraphPassId copy, firstUpload, secondUpload;
        var result = new ReadbackValues();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferSrvId source = edit.CreateBufferSrv(input, structureStride: 4);
            _ = edit.CreateBufferSrv(edit.CreateBufferInput(BufferUsages.ShaderRead), structureStride: 4);
            first = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderRead | BufferUsages.CopyDestination));
            second = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderRead | BufferUsages.CopyDestination));
            output = edit.CreatePersistentBuffer(new(4,
                BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(output);
            GraphBufferUavId destination = edit.CreateBufferUav(output, structureStride: 4);
            copy = edit.AddComputeFramePass("copy optional input", PassQueueSelection.OnQueue(support.Graphics),
                (Source: source, Destination: destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline, shader.GetSlangReflection(0).GetGlobalParamsVarLayout()!.Value, [],
                static (ref PassDefinition pass, ref (GraphBufferSrvId Source, GraphBufferUavId Destination) data) =>
                {
                    pass.Bind(data.Source, PipelineSync.ComputeShading);
                    pass.Bind(data.Destination, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                },
                static (ref ComputePassCommandScope commands, in (GraphBufferSrvId Source, GraphBufferUavId Destination) _) =>
                    commands.Dispatch(new(1, 1, 1)));
            firstUpload = edit.AddBufferUploadPass(first);
            secondUpload = edit.AddBufferUploadPass(second);
            edit.AddReadback(output, new(0, 4), result, "read optional input");
            edit.Commit();
        }
        for (int index = 0; index < 5; index++)
        {
            if (index == 3)
            {
                using RenderGraphFrame canceled = graph.BeginFrame();
                canceled.SetPassEnabled(copy, true);
                canceled.WriteBuffer(first, 0, BitConverter.GetBytes(99u));
                canceled.SetBufferInput(input, new(first, new(0, 4)));
                canceled.OrderAfter(copy, firstUpload);
            }
            ulong identity;
            RenderGraphStatistics statistics = default;
            using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics:
                (in RenderGraphDiagnosticsView view) => statistics = view.Statistics)))
            {
                bool enabled = index is 1 or 4;
                if (index == 3) Assert.False(frame.IsPassEnabled(copy));
                else frame.SetPassEnabled(copy, enabled);
                if (index == 0) frame.WriteBuffer(output, 0, BitConverter.GetBytes(0u));
                if (enabled)
                {
                    GraphBufferId source = index == 1 ? first : second;
                    frame.WriteBuffer(source, 0, BitConverter.GetBytes(index == 1 ? 7u : 9u));
                    frame.SetBufferInput(input, new(source, new(0, 4)));
                    frame.OrderAfter(copy, index == 1 ? firstUpload : secondUpload);
                }
                identity = frame.FrameIdentity;
                frame.Execute();
            }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
            Assert.Equal(index != 0, statistics.PersistentScheduleReused);
        }
        Assert.Equal(new uint[] { 0, 7, 7, 7, 9 }, result.Values.Select(value => value.Value));
    }

    [Fact]
    public void IndependentFrameSectionResourcesKeepTheirOwnBoundaryDependencies()
    {
        using var support = new ScheduleDevice();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        GraphBufferId input, color, presented;
        GraphPassId shade, present;
        var result = new ReadbackValues();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            color = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            presented = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(input);
            shade = edit.AddCopyFramePass("shade scene", PassQueueSelection.OnQueue(support.Graphics), (input, color),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Input, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Input), 0, commands.GetBuffer(data.Output), 0, 4)));
            present = edit.AddCopyFramePass("present scene", PassQueueSelection.OnQueue(support.Graphics), (color, presented),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Input, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Input), 0, commands.GetBuffer(data.Output), 0, 4)));
            edit.Commit();
        }
        ulong identity;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(input, 0, BitConverter.GetBytes(42u));
            GraphPassId complete = frame.AddGeneralPass("complete texture upload", PassQueueSelection.OnQueue(support.Graphics),
                input, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferId data) =>
                    pass.Read(data, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref GeneralPassCommandScope _, in GraphBufferId _) => { });
            frame.OrderAfter(complete, shade);
            frame.OrderAfter(present, complete);
            frame.AddReadback(new(presented, new(0, 4)), result, "capture scene");
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(42u, Assert.Single(result.Values).Value);
    }

    [Fact]
    public void BoundInputsReadTheCurrentProducerAfterItsOtherDependencies()
    {
        using var support = new ScheduleDevice();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.Graphics]);
        GraphBufferId input, intermediate, current, output;
        GraphBufferInput selected;
        var result = new ReadbackValues();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            intermediate = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            current = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            output = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(input);
            selected = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphPassId producer = edit.AddCopyFramePass("produce current value", PassQueueSelection.OnQueue(support.Graphics),
                (intermediate, current), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Input, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Input), 0, commands.GetBuffer(data.Output), 0, 4)));
            edit.AddCopyFramePass("read selected value", PassQueueSelection.OnQueue(support.Graphics), (selected, output),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Output), 0, 4));
                });
            GraphPassId dependency = edit.AddCopyFramePass("produce dependency", PassQueueSelection.OnQueue(support.Graphics),
                (input, intermediate), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Input, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Input), 0, commands.GetBuffer(data.Output), 0, 4)));
            edit.OrderAfter(producer, dependency);
            edit.AddReadback(output, new(0, 4), result, "read current producer result");
            edit.Commit();
        }
        ulong identity;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(input, 0, BitConverter.GetBytes(17u));
            frame.SetBufferInput(selected, new(current, new(0, 4)));
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(17u, Assert.Single(result.Values).Value);
    }

    private sealed class SubmissionCounter { internal uint Value; }
    private sealed class SubmittedCounter(SubmissionCounter counter, uint value) : IRenderGraphFrameExecutionObserver
    {
        internal uint Value { get; } = value;
        public void OnFrameSubmitted(in RenderGraphSubmission submission) => counter.Value = Value;
    }
    private sealed class ReadbackValues : IGraphReadbackReceiver
    {
        internal readonly List<(ulong Tag, uint Value)> Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values.Add((tag, BitConverter.ToUInt32(bytes)));
    }
    private sealed class ScheduleDevice : IDisposable
    {
        internal IGraphicsBackend Backend { get; } = new ValidationLayer(D3D12GraphicsBackend.Create());
        internal Device Device { get; }
        internal Queue Graphics { get; }
        internal Queue Compute { get; }
        internal Queue Copy { get; }
        internal ScheduleDevice()
        {
            var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
            Backend.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            Backend.TryEnumerateAdapters(options, adapters, out _);
            AdapterInfo warp = adapters.Single(adapter => !adapter.HardwareAccelerated);
            Device = Backend.CreateDevice(new(warp.Id,
                [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
                requiredFeatures: DeviceFeatures.ExternalTimelines));
            Graphics = Backend.GetQueue(Device, QueueType.Graphics);
            Compute = Backend.GetQueue(Device, QueueType.Compute);
            Copy = Backend.GetQueue(Device, QueueType.Copy);
        }
        public void Dispose() { Device.Dispose(); Backend.Dispose(); }
    }
}
