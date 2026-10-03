using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameUploadTests
{
    [Fact]
    public void NextFrameMappedAndByteWritesReachTheSameFrameReader()
    {
        using var data = new UploadBuffers();
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            new byte[] { 3, 7, 11, 19, 23, 29, 31, 37 }.CopyTo(next.GetWriteSpan(data.Values, 0, 8));
            next.WriteBuffer(data.Values, 8, new byte[] { 41, 43, 47, 53, 59, 61, 67, 71 });
            using RenderGraphFrame frame = data.Graph.BeginFrame(next);
            data.Execute(frame);
        }
        Assert.Equal(new byte[] { 3, 7, 11, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71 },
            Assert.Single(data.Results));
    }

    [Fact]
    public void RecordingWritesJoinNextFrameWritesForTheSameReader()
    {
        using var data = new UploadBuffers();
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            next.WriteBuffer(data.Values, 0, new byte[] { 2, 4, 6, 8, 10, 12, 14, 16 });
            using RenderGraphFrame frame = data.Graph.BeginFrame(next);
            frame.WriteBuffer(data.Values, 8, new byte[] { 18, 20, 22, 24, 26, 28, 30, 32 });
            data.Execute(frame);
        }
        Assert.Equal(new byte[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32 },
            Assert.Single(data.Results));
    }

    [Fact]
    public void FirstUploadDuringRecordingReachesTheSameFrameReader()
    {
        using var data = new UploadBuffers();
        using RenderGraphFrame frame = data.Graph.BeginFrame();
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(frame.GetWriteSpan(data.Values, 0, 8));
        frame.WriteBuffer(data.Values, 8, new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 });
        data.Execute(frame);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            Assert.Single(data.Results));
    }

    [Fact]
    public async Task ParallelWritesToDifferentRangesProduceCompleteData()
    {
        using var data = new UploadBuffers(64);
        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
        {
            Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
            using (next)
            {
                using var ready = new Barrier(16);
                Task[] writes = Enumerable.Range(0, 16).Select(index => Task.Factory.StartNew(() =>
                {
                    ready.SignalAndWait();
                    uint value = (uint)index + 1;
                    MemoryMarshal.Write(next.GetWriteSpan(data.Values, (ulong)index * 4, 4), in value);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                await Task.WhenAll(writes);
                using RenderGraphFrame frame = data.Graph.BeginFrame(next);
                data.Execute(frame);
            }
        }
        Assert.All(data.Results, bytes => Assert.Equal(
            new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            MemoryMarshal.Cast<byte, uint>(bytes).ToArray()));
    }

    [Fact]
    public void UnwrittenRangesKeepTheirPreviousBytes()
    {
        using var data = new UploadBuffers();
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.WriteBuffer(data.Values, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            data.Execute(frame);
        }
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            next.WriteBuffer(data.Values, 3, new byte[] { 61, 62 });
            new byte[] { 71, 72 }.CopyTo(next.GetWriteSpan(data.Values, 11, 2));
            using RenderGraphFrame frame = data.Graph.BeginFrame(next);
            data.Execute(frame);
        }
        Assert.Equal(new byte[] { 1, 2, 3, 61, 62, 6, 7, 8, 9, 10, 11, 71, 72, 14, 15, 16 }, data.Results[^1]);
    }

    [Fact]
    public void DiscardingNextFrameKeepsThePersistentBufferContent()
    {
        using var data = new UploadBuffers();
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.WriteBuffer(data.Values, 0, new byte[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80 });
            data.Execute(frame);
        }
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            next.GetWriteSpan(data.Values, 0, 8).Fill(123);
            next.WriteBuffer(data.Values, 8, new byte[] { 99, 98, 97, 96, 95, 94, 93, 92 });
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
            data.Execute(frame);
        Assert.Equal(new byte[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80 }, data.Results[^1]);
    }

    [Fact]
    public void RepeatedUploadsKeepTheGraphStructureVersion()
    {
        using var data = new UploadBuffers();
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame first, TimeSpan.Zero));
        using (first)
        {
            first.GetWriteSpan(data.Values, 0, 16).Fill(7);
            using RenderGraphFrame frame = data.Graph.BeginFrame(first);
            data.Execute(frame);
        }
        ulong version = data.Graph.StructureVersion;
        for (int index = 0; index < 4; index++)
        {
            Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
            using (next)
            {
                next.GetWriteSpan(data.Values, 0, 16).Fill(13);
                using RenderGraphFrame frame = data.Graph.BeginFrame(next);
                Assert.Equal(version, frame.StructureVersion);
                data.Execute(frame);
            }
        }
        Assert.Equal(version, data.Graph.StructureVersion);
    }

    [Fact]
    public void UploadsUseEditsCommittedBetweenOpeningAndStartingTheFrame()
    {
        using var data = new UploadBuffers();
        Assert.True(data.Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            next.GetWriteSpan(data.Values, 0, 16).Fill(17);
            using (RenderGraphEdit edit = data.Graph.BeginEdit())
            {
                GraphBufferId added = edit.CreatePersistentBuffer(new BufferDesc(8,
                    BufferUsages.CopySource | BufferUsages.CopyDestination));
                next.WriteBuffer(added, 0, new byte[] { 31, 37, 41, 43, 47, 53, 59, 61 });
                _ = edit.AddReadback(added, BufferRange.Whole, data, "Added values");
                edit.Commit();
            }
            using RenderGraphFrame frame = data.Graph.BeginFrame(next);
            data.Execute(frame);
        }
        Assert.Contains(data.Results, bytes => bytes.SequenceEqual(new byte[] { 31, 37, 41, 43, 47, 53, 59, 61 }));
    }

    [Fact]
    public void OpeningNextFrameTimesOutWhileItsGpuWorkIsInFlight()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddGeneralFramePass("Work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                0, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref int data) => { },
                static (ref GeneralPassCommandScope commands, in int data) => { });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.Execute(completions);
            Assert.False(graph.TryOpenNextFrame(out _, TimeSpan.FromMilliseconds(30)));
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        next.Dispose();
    }

    private sealed class UploadBuffers : IGraphReadbackReceiver, IDisposable
    {
        private readonly WarpGraphTestSupport _support = WarpGraphTestSupport.Create(enableValidation: true);
        internal RenderGraph Graph { get; }
        internal GraphBufferId Values { get; }
        internal List<byte[]> Results { get; } = [];

        internal UploadBuffers(int byteCount = 16)
        {
            Graph = new RenderGraph(_support.Backend, _support.Device,
                [_support.GraphicsQueue, _support.CopyQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
            using RenderGraphEdit edit = Graph.BeginEdit();
            Values = edit.CreatePersistentBuffer(new BufferDesc((ulong)byteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId observed = edit.CreatePersistentBuffer(new BufferDesc((ulong)byteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddCopyFramePass("Read values", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (Values, observed, byteCount), default,
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination, int Size) buffers) =>
                {
                    pass.Read(buffers.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(buffers.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination, int Size) buffers) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(buffers.Source), 0,
                        commands.GetBuffer(buffers.Destination), 0, (ulong)buffers.Size)));
            _ = edit.AddReadback(observed, BufferRange.Whole, this, "Observed values");
            edit.Commit();
        }

        internal void Execute(RenderGraphFrame frame)
        {
            var completions = new QueueCompletion[Graph.MaximumQueueCompletionCount];
            frame.Execute(completions);
            _support.Wait(completions);
            Graph.CollectCompleted();
        }

        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Results.Add(bytes.ToArray());
        public void Dispose()
        {
            Graph.Dispose();
            _support.Dispose();
        }
    }
}
