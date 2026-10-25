using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ExternalBufferUseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherConsumerCanLeaveWithoutBreakingTheOther(bool releaseIntroducerFirst)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer buffer = CreateBuffer(support);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using BufferSrv view = CreateView(support, buffer);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var uses = new GraphBufferUse[2];
        var inputs = new GraphParameterInput[2];
        var passes = new GraphPassId[2];
        GraphBufferId destination;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            destination = edit.RegisterExternalBuffer(result, Boundary(result, ResourceContentState.Undefined));
            edit.Commit();
        }
        for (int index = 0; index < 2; index++)
        {
            using RenderGraphEdit edit = graph.BeginEdit();
            uses[index] = edit.AcquireExternalBufferUse(buffer, Boundary(buffer, ResourceContentState.Undefined));
            inputs[index] = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            passes[index] = edit.AddCopyFramePass("copy a consumer's borrowed material buffer",
                PassQueueSelection.Exact(support.GraphicsQueue),
                (uses[index].Buffer, destination, inputs[index], (ulong)index * 4),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass,
                    ref (GraphBufferId Source, GraphBufferId Destination, GraphParameterInput Input, ulong Offset) data) =>
                {
                    pass.Bind(data.Input, PipelineSync.ComputeShading);
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, new BufferRange(data.Offset, 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, static (ref CopyPassCommandScope commands,
                    in (GraphBufferId Source, GraphBufferId Destination, GraphParameterInput Input, ulong Offset) data) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), data.Offset, 4)));
            edit.Commit();
        }
        Assert.NotSame(uses[0], uses[1]);
        Assert.Equal(uses[0].Buffer, uses[1].Buffer);
        ExecuteConsumers(graph, support, uses[0].Buffer, view, inputs, 42);
        Assert.Equal(new byte[] { 42, 0, 0, 0, 42, 0, 0, 0 }, Read(support, result));

        int first = releaseIntroducerFirst ? 0 : 1;
        int remaining = 1 - first;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(passes[first]);
            edit.ReleaseExternalBufferUse(uses[first]);
            edit.Commit();
        }
        Assert.True(graph.TryGetBufferId(buffer, out GraphBufferId survivor));
        Assert.Equal(uses[remaining].Buffer, survivor);
        ExecuteConsumers(graph, support, survivor, view, [inputs[remaining]], 99);
        byte[] expected = [42, 0, 0, 0, 42, 0, 0, 0];
        expected[remaining * 4] = 99;
        Assert.Equal(expected, Read(support, result));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            bool rejected = false;
            try { edit.ReleaseExternalBufferUse(uses[first]); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            edit.Remove(passes[remaining]);
            edit.ReleaseExternalBufferUse(uses[remaining]);
            edit.Commit();
        }
        Assert.False(graph.TryGetBufferId(buffer, out _));
        graph.Dispose();
        using BufferSrv stillCallerOwned = CreateView(support, buffer);
    }

    [Fact]
    public void AbandoningAnAcquireAndReleasePreservesTheAcceptedConsumer()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer acceptedBuffer = CreateBuffer(support);
        using Buffer candidateBuffer = CreateBuffer(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphBufferUse accepted;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            accepted = edit.AcquireExternalBufferUse(acceptedBuffer, Boundary(acceptedBuffer));
            edit.Commit();
        }
        RenderGraphEdit abandoned = graph.BeginEdit();
        GraphBufferUse candidate = abandoned.AcquireExternalBufferUse(candidateBuffer, Boundary(candidateBuffer));
        abandoned.ReleaseExternalBufferUse(accepted);
        abandoned.Dispose();
        abandoned.Dispose();
        Assert.False(abandoned.IsAccepted);
        Assert.True(graph.TryGetBufferId(acceptedBuffer, out GraphBufferId restored));
        Assert.Equal(accepted.Buffer, restored);
        Assert.False(graph.TryGetBufferId(candidateBuffer, out _));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            bool rejected = false;
            try { edit.ReleaseExternalBufferUse(candidate); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            edit.ReleaseExternalBufferUse(accepted);
            edit.Commit();
        }
        graph.Dispose();
        using BufferSrv acceptedStillBorrowed = CreateView(support, acceptedBuffer);
        using BufferSrv candidateStillBorrowed = CreateView(support, candidateBuffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquiringAndReleasingWithinOneEditLeavesNoRegistration(bool commit)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = CreateBuffer(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        RenderGraphEdit edit = graph.BeginEdit();
        GraphBufferUse use = edit.AcquireExternalBufferUse(buffer, Boundary(buffer));
        edit.ReleaseExternalBufferUse(use);
        if (commit) edit.Commit();
        edit.Dispose();
        Assert.Equal(commit, edit.IsAccepted);
        Assert.False(graph.TryGetBufferId(buffer, out _));
        graph.Dispose();
        using BufferSrv stillCallerOwned = CreateView(support, buffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUseProtectsButDoesNotOwnAPreexistingRegistration(bool graphOwned)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer? callerBuffer = graphOwned ? null : CreateBuffer(support);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        Buffer? observed = null;
        GraphBufferId registered;
        GraphPassId uploadPass;
        GraphPassId readPass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            if (graphOwned)
            {
                registered = edit.CreatePersistentBuffer(new BufferDesc(4,
                    BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination));
            }
            else
            {
                GraphBufferUse initial = edit.AcquireExternalBufferUse(callerBuffer!, Boundary(callerBuffer!));
                registered = edit.RegisterExternalBuffer(callerBuffer!, Boundary(callerBuffer!));
                Assert.Equal(initial.Buffer, registered);
                edit.ReleaseExternalBufferUse(initial);
            }
            uploadPass = edit.AddBufferUploadPass(registered);
            GraphBufferId destination = edit.RegisterExternalBuffer(result, Boundary(result, ResourceContentState.Undefined));
            readPass = edit.AddCopyFramePass("copy a preexisting registration",
                PassQueueSelection.Exact(support.GraphicsQueue), (registered, destination),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    Buffer source = commands.GetBuffer(data.Source);
                    observed = source;
                    commands.CopyBuffer(new BufferCopy(source, 0, commands.GetBuffer(data.Destination), 0, 4));
                });
            edit.Commit();
        }
        Execute(graph, support, registered);
        Assert.Equal(new byte[] { 42, 0, 0, 0 }, Read(support, result));
        Buffer native = Assert.IsAssignableFrom<Buffer>(observed);
        Assert.True(graph.TryGetBufferId(native, out GraphBufferId original));
        Assert.Equal(registered, original);
        GraphBufferUse use;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            use = edit.AcquireExternalBufferUse(native, Boundary(native));
            edit.Commit();
        }
        Assert.Equal(registered, use.Buffer);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            bool rejected = false;
            try { edit.Remove(registered); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            edit.Commit();
        }
        Assert.True(graph.TryGetBufferId(native, out GraphBufferId protectedId));
        Assert.Equal(registered, protectedId);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.ReleaseExternalBufferUse(use);
            edit.Commit();
        }
        Assert.True(graph.TryGetBufferId(native, out GraphBufferId retained));
        Assert.Equal(registered, retained);
        Execute(graph, support, registered, 99);
        Assert.Same(native, observed);
        Assert.Equal(new byte[] { 99, 0, 0, 0 }, Read(support, result));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(readPass);
            edit.Remove(uploadPass);
            edit.Remove(registered);
            edit.Commit();
        }
        Assert.False(graph.TryGetBufferId(native, out _));
        graph.Dispose();
        if (callerBuffer is not null)
        {
            using BufferSrv stillCallerOwned = CreateView(support, callerBuffer);
        }
    }

    [Fact]
    public void ReservedFramesAllowReconciliationButStartedFramesRejectUseChanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = CreateBuffer(support);
        using Buffer rejectedBuffer = CreateBuffer(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphBufferUse accepted;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            accepted = edit.AcquireExternalBufferUse(buffer, Boundary(buffer));
            edit.Commit();
        }
        Assert.True(graph.TryOpenNextFrame(out var next, TimeSpan.Zero));
        ulong version;
        using (next)
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                GraphBufferUse replacement = edit.AcquireExternalBufferUse(buffer, Boundary(buffer));
                edit.ReleaseExternalBufferUse(accepted);
                edit.Commit();
                accepted = replacement;
            }
            version = graph.StructureVersion;
            using RenderGraphFrame frame = graph.BeginFrame(next);
            using RenderGraphEdit activeEdit = graph.BeginEdit();
            bool acquireRejected = false;
            try { _ = activeEdit.AcquireExternalBufferUse(rejectedBuffer, Boundary(rejectedBuffer)); }
            catch (InvalidOperationException) { acquireRejected = true; }
            Assert.True(acquireRejected);
            bool releaseRejected = false;
            try { activeEdit.ReleaseExternalBufferUse(accepted); }
            catch (InvalidOperationException) { releaseRejected = true; }
            Assert.True(releaseRejected);
            activeEdit.Commit();
            Assert.True(activeEdit.IsAccepted);
        }
        Assert.Equal(version, graph.StructureVersion);
        Assert.True(graph.TryGetBufferId(buffer, out GraphBufferId retained));
        Assert.Equal(accepted.Buffer, retained);
        Assert.False(graph.TryGetBufferId(rejectedBuffer, out _));
        graph.Dispose();
        using BufferSrv acceptedStillBorrowed = CreateView(support, buffer);
        using BufferSrv rejectedStillBorrowed = CreateView(support, rejectedBuffer);
    }

    private static Buffer CreateBuffer(WarpGraphTestSupport support) => support.Backend.CreateBuffer(support.Device,
        new BufferDesc(4, BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination));

    private static BufferSrv CreateView(WarpGraphTestSupport support, Buffer buffer) =>
        support.Backend.CreateBufferSrv(support.Device, new BufferSrvDesc(buffer, BufferRange.Whole));

    private static BufferBoundaryState[] Boundary(Buffer buffer,
        ResourceContentState content = ResourceContentState.Undefined) =>
        [new BufferBoundaryState(BufferRange.Whole, buffer.InitialSync, buffer.InitialAccess, content)];

    private static byte[] Read(WarpGraphTestSupport support, Buffer buffer)
    {
        using MappedBuffer mapping = support.Backend.Map(buffer, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        return mapping.Bytes.ToArray();
    }

    private static void Execute(RenderGraph graph, WarpGraphTestSupport support, GraphBufferId uploadTarget, byte value = 42)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        frame.WriteBuffer(uploadTarget, 0, [value, 0, 0, 0]);
        frame.Execute(completions);
        support.Wait(completions);
    }

    private static void ExecuteConsumers(RenderGraph graph, WarpGraphTestSupport support,
        GraphBufferId source, BufferSrv view, ReadOnlySpan<GraphParameterInput> inputs, byte value)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        GraphBufferSlice upload = frame.Upload<byte>([value, 0, 0, 0], BufferUsages.CopySource);
        GraphPassId writer = frame.AddCopyPass("upload borrowed material bytes",
            PassQueueSelection.Exact(support.GraphicsQueue), (upload, source), default,
            static (ref PassDefinition pass, ref (GraphBufferSlice Upload, GraphBufferId Target) data) =>
            {
                pass.Read(data.Upload.Buffer, data.Upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Target, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }, static (ref CopyPassCommandScope commands, in (GraphBufferSlice Upload, GraphBufferId Target) data) =>
                commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Upload.Buffer), data.Upload.Range.Offset,
                    commands.GetBuffer(data.Target), 0, 4)));
        foreach (GraphParameterInput input in inputs)
        {
            frame.SetPersistentParameterInput(input, ResourceBinding.ReadOnlyBuffer(view));
            frame.OrderAfter(input, writer);
        }
        frame.Execute(completions);
        support.Wait(completions);
    }
}
