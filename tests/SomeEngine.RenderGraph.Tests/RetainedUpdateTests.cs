using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RetainedUpdateTests
{


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







    private static GraphPassId AddPass(RenderGraphEdit edit, Queue queue, string label) =>
        edit.AddCopyPass<byte, byte>(label, PassQueueSelection.Exact(queue), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref byte _) => { },
            static (ref CopyPassCommandScope _, in byte _, in byte _) => { });
}
