using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RetainedUpdateTests
{
    [Fact]
    public void BindingStreamReplacementKeepsPassIdentityAndPersistsAcrossExecutions()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        GraphPassId pass;
        GraphBufferId buffer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreateBuffer(new BufferDesc(4, BufferUsages.CopyDestination));
            pass = edit.AddCopyFramePass("retained bindings", PassQueueSelection.Exact(support.GraphicsQueue), 0,
                default, static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.SetPassParameterBindings(pass, [GraphParameterResourceBinding.Null(ResourceBindingType.Sampler)]);
            edit.Commit();
        }
        for (int frameIndex = 0; frameIndex < 7; frameIndex++)
        {
            if (frameIndex == 3)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                PassDefinition definition = edit.GetPass(pass);
                definition.Write(buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                edit.SetPassParameterBindings(pass,
                    [GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv),
                     GraphParameterResourceBinding.Null(ResourceBindingType.Sampler)]);
                edit.Commit();
            }
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            GraphPass declaration = graph.StructureIndex.Structure.Passes.Get(pass.Value);
            Assert.Equal(frameIndex < 3 ? 1 : 2, declaration.ParameterBindings.Count);
            Assert.Equal(frameIndex < 3 ? 0 : 1, declaration.Accesses.Count);
        }
    }

    [Fact]
    public void RemovingOneResourceDependencyKeepsTheOtherProducerDependency()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] recorded = [0, 0];
        GraphBufferAccessId leftRead = default, rightRead = default;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId left = edit.CreateBuffer(new BufferDesc(4, BufferUsages.CopyDestination | BufferUsages.CopySource));
            GraphBufferId right = edit.CreateBuffer(new BufferDesc(4, BufferUsages.CopyDestination | BufferUsages.CopySource));
            edit.AddCopyFramePass("shared producer", PassQueueSelection.Exact(support.GraphicsQueue), recorded,
                new PassOptions(Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition pass, ref int[] _) =>
                {
                    pass.Write(left, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    pass.Write(right, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope _, in int[] counts) => counts[0]++);
            edit.AddCopyFramePass("shared consumer", PassQueueSelection.Exact(support.GraphicsQueue), recorded,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition pass, ref int[] _) =>
                {
                    leftRead = pass.Read(left, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    rightRead = pass.Read(right, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                },
                static (ref CopyPassCommandScope _, in int[] counts) => counts[1]++);
            edit.Commit();
        }
        Execute();
        Assert.Equal(new[] { 1, 1 }, recorded);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(leftRead);
            edit.Commit();
        }
        Execute();
        Assert.Equal(new[] { 2, 2 }, recorded);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(rightRead);
            edit.Commit();
        }
        Execute();
        Assert.Equal(new[] { 2, 3 }, recorded);

        void Execute()
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
    }

    [Fact]
    public void ExecutionCanGrowClearAndRecreateItsPasses()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var passes = new List<GraphPassId>();
        int[] recorded = [0];
        foreach (int requested in new[] { 1, 9, 0, 3, 0, 1 })
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                while (passes.Count > requested)
                {
                    edit.Remove(passes[^1]);
                    passes.RemoveAt(passes.Count - 1);
                }
                while (passes.Count < requested)
                    passes.Add(edit.AddCopyFramePass("grow and remove", PassQueueSelection.Exact(support.GraphicsQueue), recorded,
                        new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                        static (ref PassDefinition _, ref int[] _) => { },
                        static (ref CopyPassCommandScope _, in int[] counts) => counts[0]++));
                edit.Commit();
            }
            recorded[0] = 0;
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            Assert.Equal(requested, recorded[0]);
        }
    }

    [Fact]
    public void PassDataAndEnablementPersistAcrossExecutionsInFlight()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 3));
        var observed = new List<int>();
        GraphPassId reader, disabled;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            reader = edit.AddCopyPass<List<int>, int>("retained callback data", PassQueueSelection.Exact(support.GraphicsQueue),
                observed, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref List<int> _) => { },
                static (ref CopyPassCommandScope _, in List<int> values, in int data) => values.Add(data));
            disabled = edit.AddCopyPass<byte, byte>("retained disabled state", PassQueueSelection.Exact(support.GraphicsQueue),
                0, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref byte _) => { },
                static (ref CopyPassCommandScope _, in byte _, in byte _) => throw new InvalidOperationException("Disabled pass recorded."));
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 7; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            if (index == 0)
            {
                frame.SetPassData(reader, 17);
                frame.SetPassEnabled(disabled, false);
            }
            if (index == 4) frame.SetPassData(reader, 29);
            frame.Execute(completions);
            support.Wait(completions);
        }
        Assert.Equal(new[] { 17, 17, 17, 17, 29, 29, 29 }, observed);
    }

    [Fact]
    public void LatePrerequisitePreservesInitializationOrderForSharedResource()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer output = support.Backend.CreateBuffer(support.Device, new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphPassId initialize, consume;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId result = edit.RegisterExternalBuffer(output,
                [new BufferBoundaryState(new(0, 4), output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            initialize = edit.AddCopyFramePass("initialize before consumers", PassQueueSelection.Exact(support.GraphicsQueue), buffer, default,
                static (ref PassDefinition pass, ref GraphBufferId resource) => pass.Write(resource, BufferRange.Whole,
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphBufferId resource) => commands.ClearBuffer(commands.GetBuffer(resource), BufferRange.Whole, 0x12345678));
            consume = edit.AddCopyFramePass("consume initialized contents", PassQueueSelection.Exact(support.GraphicsQueue),
                (Buffer: buffer, Result: result), default,
                static (ref PassDefinition pass, ref (GraphBufferId Buffer, GraphBufferId Result) resources) =>
                {
                    pass.Read(resources.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(resources.Result, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Buffer, GraphBufferId Result) resources) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(resources.Buffer), 0, commands.GetBuffer(resources.Result), 0, 4)));
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 4; index++)
        {
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                GraphPassId prerequisite = frame.AddCopyPass("late prerequisite", PassQueueSelection.Exact(support.GraphicsQueue),
                    (byte)0, new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition _, ref byte _) => { },
                    static (ref CopyPassCommandScope _, in byte _) => { });
                frame.OrderAfter(initialize, prerequisite);
                frame.Execute(completions);
            }
            support.Wait(completions);
            using MappedBuffer data = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            Assert.Equal(0x12345678u, System.Runtime.InteropServices.MemoryMarshal.Read<uint>(data.Bytes));
        }
    }

    [Fact]
    public void LabelsAndEmptyEditsKeepTheDependencyStructure()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphPassId left, right;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            left = AddPass(edit, support.GraphicsQueue, "left");
            right = AddPass(edit, support.GraphicsQueue, "right");
            edit.Commit();
        }
        GraphStructureIndex before = graph.StructureIndex;
        GraphPass unrelated = before.Structure.Passes.Get(right.Value);
        ulong version = graph.StructureVersion;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassLabel(left, "changed");
            edit.Commit();
        }
        Assert.Equal(version, graph.StructureVersion);
        Assert.Equal("left", before.Structure.Passes.Get(left.Value).Label);
        Assert.Equal("changed", graph.StructureIndex.Structure.Passes.Get(left.Value).Label);
        Assert.Same(unrelated, graph.StructureIndex.Structure.Passes.Get(right.Value));
        Assert.Same(before.DependencyPredecessors, graph.StructureIndex.DependencyPredecessors);
        Assert.Same(before.Schedule, graph.StructureIndex.Schedule);
        GraphStructureIndex changed = graph.StructureIndex;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassLabel(left, "changed");
            edit.Commit();
        }
        Assert.Same(changed, graph.StructureIndex);
        using (RenderGraphEdit edit = graph.BeginEdit()) edit.Commit();
        Assert.Same(changed, graph.StructureIndex);
        Assert.Equal(version, graph.StructureVersion);
    }

    [Fact]
    public void BackwardEdgeReordersItsConnectedNodesAndRejectsCyclesAtomically()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var passes = new GraphPassId[256];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int i = 0; i < passes.Length; i++) passes[i] = AddPass(edit, support.GraphicsQueue, "pass");
            edit.OrderAfter(passes[1], passes[0]);
            edit.OrderAfter(passes[^1], passes[^2]);
            edit.Commit();
        }
        GraphStructureIndex before = graph.StructureIndex;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(passes[0], passes[^1]);
            edit.Commit();
        }
        GraphStructureIndex after = graph.StructureIndex;
        for (int i = 2; i < passes.Length - 2; i++)
        {
            Assert.Equal(before.Positions[passes[i].Value], after.Positions[passes[i].Value]);
            Assert.Same(before.Structure.Passes.Get(passes[i].Value), after.Structure.Passes.Get(passes[i].Value));
        }
        foreach ((GraphIdentity predecessor, GraphIdentity[] consumers) in after.DependencySuccessors)
            foreach (GraphIdentity consumer in consumers)
                Assert.True(after.Positions[predecessor] < after.Positions[consumer]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(passes[^2], passes[1]);
            string? failure = null;
            try { edit.Commit(); }
            catch (InvalidOperationException exception) { failure = exception.Message; }
            Assert.Contains("RG5001", failure);
        }
        Assert.Same(after, graph.StructureIndex);
    }

    [Fact]
    public void SingleNodeEditDoesNotCopyUnrelatedRowsOrAllocateForTheWholeGraph()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        long small = Measure(64);
        long large = Measure(2048);
        // Collection path copying may grow logarithmically. A whole table or row
        // copy would add hundreds of kilobytes here, as the former implementation did.
        Assert.True(large <= small + 16_384, $"64 nodes: {small} bytes; 2048 nodes: {large} bytes.");

        long Measure(int count)
        {
            using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
            GraphPassId first = default, last = default;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                for (int i = 0; i < count; i++)
                {
                    last = AddPass(edit, support.GraphicsQueue, "pass");
                    if (i == 0) first = last;
                }
                edit.Commit();
            }
            GraphPass unrelated = graph.StructureIndex.Structure.Passes.Get(last.Value);
            long minimum = long.MaxValue;
            for (int i = 0; i < 12; i++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    edit.SetPassLabel(first, i % 2 == 0 ? "a" : "b");
                    edit.Commit();
                }
                if (i >= 4) minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - bytes);
                Assert.Same(unrelated, graph.StructureIndex.Structure.Passes.Get(last.Value));
            }
            return minimum;
        }
    }

    private static GraphPassId AddPass(RenderGraphEdit edit, Queue queue, string label) =>
        edit.AddCopyPass<byte, byte>(label, PassQueueSelection.Exact(queue), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref byte _) => { },
            static (ref CopyPassCommandScope _, in byte _, in byte _) => { });
}
