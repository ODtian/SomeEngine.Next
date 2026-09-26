using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class InstancePreparationTests
{
    [Fact]
    public async Task CompletingAnEarlierFrameAllowsWritesWhileALaterFrameStillReads()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new(QueueType.Graphics), new(QueueType.Compute)]);
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var token = builder.Register<Vector4>("Frame overlap tests", new("test.frame.values"),
            new("test.float4", 16, 16, 16, 1));
        var column = builder.Freeze();
        var complete = new RenderInstancePropertyLayoutBuilder();
        complete.Include(column);
        var unchanged = complete.Register<Vector4>("Frame overlap tests", new("test.frame.unchanged"),
            new("test.float4", 16, 16, 16, 1));
        var layout = complete.Freeze();
        var property = layout.Resolve(token);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            layout, new RenderInstanceOptions { RowCapacity = 1, BatchCapacity = 1 });
        var writer = new Writer(layout, column, column.Resolve(token), layout.Resolve(unchanged), 1);
        var reader = new Reader(writer, layout);
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(writer);
        using var frames = new RenderFrameSystems(world, instances);
        frames.Add(reader);
        Queue graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        Queue compute = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(test.Backend, test.Device, [graphics]);
        using var firstGate = test.Backend.CreateExternalTimeline(test.Device, 0);
        using var secondGate = test.Backend.CreateExternalTimeline(test.Device, 0);
        using var firstResult = test.Backend.CreateBuffer(test.Device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        using var secondResult = test.Backend.CreateBuffer(test.Device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        using var thirdResult = test.Backend.CreateBuffer(test.Device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? rewrite = null;
        Vector4 first = new(1, 2, 3, 4), second = new(5, 6, 7, 8), third = new(9, 10, 11, 12);
        try
        {
            Write(first);
            QueueCompletion firstRead = SubmitRead(firstGate, firstResult);
            Write(second);
            QueueCompletion secondRead = SubmitRead(secondGate, secondResult);
            Assert.False(test.Backend.IsComplete(firstRead));
            Assert.False(test.Backend.IsComplete(secondRead));
            rewrite = Task.Run(() =>
            {
                writing.SetResult();
                Write(third);
            });
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task stillReading = Task.Delay(TimeSpan.FromMilliseconds(25));
            Assert.Same(stillReading, await Task.WhenAny(rewrite, stillReading));
            Release(firstGate);
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(firstRead, TimeSpan.FromSeconds(5)));
            await rewrite.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(test.Backend.IsComplete(secondRead));
            Assert.Equal(first, ReadValue(firstResult));

            Release(secondGate);
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(secondRead, TimeSpan.FromSeconds(5)));
            Assert.Equal(second, ReadValue(secondResult));
            QueueCompletion thirdRead = SubmitRead(null, thirdResult);
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(thirdRead, TimeSpan.FromSeconds(5)));
            Assert.Equal(third, ReadValue(thirdResult));
        }
        finally
        {
            Release(firstGate);
            Release(secondGate);
            try { if (rewrite is not null) await rewrite; }
            finally
            {
                test.Coordinator.WaitForTrackedSubmissions();
                Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
                using (shutdown)
                {
                    prepare.Shutdown(shutdown);
                    instances.Shutdown(shutdown);
                    shutdown.Commit();
                }
                prepare.Dispose();
                instances.Dispose();
            }
        }

        void Write(Vector4 value)
        {
            writer.Values[0] = value;
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                prepare.ApplyExtraction(scope);
                prepare.Update(scope);
                scope.Commit();
            }
        }

        QueueCompletion SubmitRead(ExternalTimeline? gate, SomeEngine.Graphics.Buffer result)
        {
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            using (frame)
            using (var graphFrame = graph.BeginFrame())
            using (CommandContext commands = test.Backend.CreateCommandContext(test.Device, new(QueueType.Graphics, 0, 1)))
            {
                frames.Update(frame, graphFrame);
                RenderInstanceBatchView view = reader.View;
                uint address;
                using (var metadata = test.Backend.Map(view.Metadata, MapType.Read, view.MetadataRange))
                    address = MemoryMarshal.Read<uint>(metadata.Bytes[(property.Descriptor.MetadataWordOffset * 4)..])
                        & RenderInstanceLinearMetadata.AddressMask;
                test.Backend.Begin(commands);
                test.Backend.CopyBuffer(commands, new(view.PropertyData!, address, result, 0, 16));
                using RecordedCommands recorded = test.Backend.End(commands);
                QueueCompletion completion = test.Backend.Submit(graphics,
                    new([], gate is null ? [] : [new(gate, 1)], [recorded], [], []));
                frame.Complete([completion]);
                return completion;
            }
        }

        void Release(ExternalTimeline gate) => test.Backend.Submit(compute, new([], [], [], [], [new(gate, 1)]));

        Vector4 ReadValue(SomeEngine.Graphics.Buffer buffer)
        {
            using var result = test.Backend.Map(buffer, MapType.Read, BufferRange.Whole);
            return MemoryMarshal.Read<Vector4>(result.Bytes);
        }
    }

    [Theory]
    [InlineData(1, false, 2)]
    [InlineData(37, false, 74)]
    [InlineData(129, false, 258)]
    [InlineData(1, true, 2)]
    [InlineData(37, true, 74)]
    [InlineData(129, true, 258)]
    [InlineData(37, false, 65536)]
    [InlineData(129, true, 65536)]
    public void RepeatedWritesRestoreCanceledValuesAndPreserveUntouchedRows(int count, bool replaceColumn, int capacity)
    {
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var token = builder.Register<Vector4>("Instance preparation tests", new("test.instance.values"),
            new("test.float4", 16, 16, 16, 1));
        var column = builder.Freeze();
        var complete = new RenderInstancePropertyLayoutBuilder();
        complete.Include(column);
        var unchangedToken = complete.Register<Vector4>("Instance preparation tests", new("test.instance.unchanged"),
            new("test.float4", 16, 16, 16, 1));
        var layout = complete.Freeze();
        var property = layout.Resolve(token);
        var unchanged = layout.Resolve(unchangedToken);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            layout, new RenderInstanceOptions { RowCapacity = capacity, BatchCapacity = 16 });
        var writer = new Writer(layout, column, column.Resolve(token), unchanged, count);
        var reader = new Reader(writer, layout);
        var otherWriter = new Writer(layout, column, column.Resolve(token), unchanged, count)
        {
            Values = Enumerable.Range(0, count).Select(row => new Vector4(-19, row, 17, 13)).ToArray(),
        };
        var otherReader = new Reader(otherWriter, layout);
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(writer);
        prepare.Add(otherWriter);
        using var frames = new RenderFrameSystems(world, instances);
        frames.Add(reader);
        frames.Add(otherReader);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(test.Backend, test.Device,
            [test.Backend.GetQueue(test.Device, QueueType.Graphics)]);
        var transfer = instances.GetDataTransfer(graph);
        var otherTransfer = instances.GetDataTransfer(graph);
        using var copiedData = test.Backend.CreateBuffer(test.Device,
            new(checked((ulong)instances.PropertyDataBytes), BufferUsages.CopyDestination), MemoryType.Readback);
        try
        {
            Write();
            Vector4[] expected = writer.Values.ToArray();
            Read(cancel: true);
            Assert.Equal(expected, Read());
            for (int iteration = 0; iteration < 4; iteration++)
            {
                writer.Values = Enumerable.Range(0, count).Select(row => new Vector4(iteration + 10, row, -row, 7)).ToArray();
                writer.Cancel = true;
                writer.ReplacedProperties = replaceColumn ? column : null;
                Write();
                Assert.Equal(expected, Read());
                writer.Cancel = false;
                writer.OneRow = true;
                writer.ReplacedProperties = null;
                Write();
                expected[count / 2] = writer.Values[count / 2];
                Read(cancel: true);
                Assert.Equal(expected, Read());
                writer.OneRow = false;
            }
            writer.ReplacedProperties = replaceColumn ? column : null;
            if (replaceColumn && count > 1)
            {
                writer.OneRow = true;
                Assert.Throws<InvalidOperationException>(Write);
                writer.Cancel = true;
                Write();
                writer.Cancel = false;
                Assert.Equal(expected, Read());
                writer.OneRow = false;
            }
            Write();
            expected = writer.Values.ToArray();
            Assert.Equal(expected, Read());
            writer.FailAfterWrite = true;
            Assert.Throws<ArgumentException>(Write);
            writer.FailAfterWrite = false;
            writer.Cancel = true;
            Write();
            Assert.Equal(expected, Read());
        }
        finally
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            instances.Dispose();
        }

        void Write()
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                Assert.False(prepare.ApplyExtraction(scope));
                Assert.False(prepare.ApplyExtraction(scope));
                prepare.Update(scope);
                scope.Commit();
            }
        }

        Vector4[] Read(bool cancel = false)
        {
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            using (frame)
            using (var commands = graph.BeginFrame())
            {
                frames.Update(frame, commands);
                var view = reader.View;
                var staging = commands;
                transfer.Stage(ref staging, writer.Batch!, view);
                otherTransfer.Stage(ref staging, otherWriter.Batch!, otherReader.View);
                // Duplicate consumers must share the same transferred data. Canceled frames
                // must leave every new batch value available for the following attempt.
                transfer.Stage(ref staging, writer.Batch!, view);
                if (cancel) return [];
                var copy = new InstanceReadback(transfer.Data, commands.Import(copiedData, []),
                    checked((ulong)instances.PropertyDataBytes));
                commands.AddCopyPass("Read transferred instances", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    copy, new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref InstanceReadback data) =>
                    {
                        pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                        pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    }, InstanceReadback.Record);
                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int submitted = commands.Execute(completions);
                frame.Complete(completions.AsSpan(0, submitted));
                foreach (QueueCompletion completion in completions.AsSpan(0, submitted))
                    Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
                using var result = test.Backend.Map(copiedData, MapType.Read, BufferRange.Whole);
                byte[] copied = result.Bytes.ToArray();
                using (var otherMetadata = test.Backend.Map(otherReader.View.Metadata, MapType.Read, otherReader.View.MetadataRange))
                {
                    uint otherWord = MemoryMarshal.Read<uint>(otherMetadata.Bytes[(property.Descriptor.MetadataWordOffset * 4)..]);
                    Assert.Equal(otherWriter.Values, MemoryMarshal.Cast<byte, Vector4>(copied.AsSpan(
                        checked((int)(otherWord & RenderInstanceLinearMetadata.AddressMask)), count * 16)).ToArray());
                }
                using var metadata = test.Backend.Map(view.Metadata, MapType.Read, view.MetadataRange);
                uint word = MemoryMarshal.Read<uint>(metadata.Bytes[(property.Descriptor.MetadataWordOffset * 4)..]);
                Assert.NotEqual(0u, word & RenderInstanceLinearMetadata.PerInstanceBit);
                uint sharedWord = MemoryMarshal.Read<uint>(metadata.Bytes[(unchanged.Descriptor.MetadataWordOffset * 4)..]);
                Assert.Equal(0u, sharedWord & RenderInstanceLinearMetadata.PerInstanceBit);
                Assert.Equal(new Vector4(9, 8, 7, 6),
                    MemoryMarshal.Read<Vector4>(copied.AsSpan((int)(sharedWord & RenderInstanceLinearMetadata.AddressMask))));
                return MemoryMarshal.Cast<byte, Vector4>(copied.AsSpan(
                    checked((int)(word & RenderInstanceLinearMetadata.AddressMask)), count * 16)).ToArray();

            }
        }
    }

    [Fact]
    public void AbandoningExtractionOnlyDoesNotInvalidateGpuPreparation()
    {
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            RenderInstanceTransformProperties.Layout, new RenderInstanceOptions { RowCapacity = 1, BatchCapacity = 1 });
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        try
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope) Assert.False(prepare.ApplyExtraction(scope));
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            frame.Dispose();
        }
        finally
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            instances.Dispose();
        }
    }

    private sealed class Writer(RenderInstancePropertyLayout layout, RenderInstancePropertyLayout column,
        ResolvedRenderInstanceProperty<Vector4> property, ResolvedRenderInstanceProperty<Vector4> unchanged,
        int count) : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal Vector4[] Values = Enumerable.Range(0, count).Select(row => new Vector4(1, row, 3, 4)).ToArray();
        internal bool Cancel;
        internal bool OneRow;
        internal bool FailAfterWrite;
        internal RenderInstancePropertyLayout? ReplacedProperties;

        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var write = Batch is null ? context.AllocateBatch(layout, count) : context.RewriteBatch(Batch, column, ReplacedProperties);
            if (Batch is null) write.OpenWrite(layout).BindShared(unchanged, new Vector4(9, 8, 7, 6));
            var all = write.OpenWrite(column);
            all.BindPerInstance(property);
            if (OneRow) all.Write(property, count / 2, Values[count / 2]);
            else all.Write(property, Values);
            if (FailAfterWrite) all.Write(property, Values.AsSpan(0, count - 1));
            if (!Cancel) Batch = write.Publish();
        }
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (Batch is not null) context.Retire(Batch);
            Batch = null;
        }
    }

    [Fact]
    public void SharedTransferCombinesBatchesWhileAnEarlierFrameStillReads()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new(QueueType.Graphics), new(QueueType.Compute)]);
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var value = builder.Register<Vector4>("Transfer tests", new("test.transfer.value"), new("test.float4", 16, 16, 16, 1));
        var column = builder.Freeze();
        var complete = new RenderInstancePropertyLayoutBuilder();
        complete.Include(column);
        var shared = complete.Register<Vector4>("Transfer tests", new("test.transfer.shared"), new("test.float4", 16, 16, 16, 1));
        var layout = complete.Freeze();
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            layout, new RenderInstanceOptions { RowCapacity = 64, BatchCapacity = 4 });
        var first = new Writer(layout, column, column.Resolve(value), layout.Resolve(shared), 1);
        var second = new Writer(layout, column, column.Resolve(value), layout.Resolve(shared), 1);
        var firstReader = new Reader(first, layout);
        var secondReader = new Reader(second, layout);
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        int firstSystem = prepare.Add(first);
        prepare.Add(second);
        using var frames = new RenderFrameSystems(world, instances);
        frames.Add(firstReader);
        frames.Add(secondReader);
        Queue graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        Queue compute = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(test.Backend, test.Device, [graphics]);
        var transfer = instances.GetDataTransfer(graph);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        using var earlierData = test.Backend.CreateBuffer(test.Device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        using var copiedData = test.Backend.CreateBuffer(test.Device,
            new(checked((ulong)instances.PropertyDataBytes), BufferUsages.CopyDestination), MemoryType.Readback);
        var submitted = new List<QueueCompletion>();
        try
        {
            Write();
            Assert.True(test.Coordinator.TryBeginFrame(out var earlierFrame));
            using (earlierFrame)
            using (var unusedGraphFrame = graph.BeginFrame())
            using (var commands = test.Backend.CreateCommandContext(test.Device, new(QueueType.Graphics, 0, 1)))
            {
                frames.Update(earlierFrame, unusedGraphFrame);
                test.Backend.Begin(commands);
                test.Backend.CopyBuffer(commands, new(firstReader.View.PropertyData!, Address(firstReader.View), earlierData, 0, 16));
                using var recorded = test.Backend.End(commands);
                QueueCompletion completion = test.Backend.Submit(graphics, new([], [new(gate, 1)], [recorded], [], []));
                submitted.Add(completion);
                earlierFrame.Complete([completion]);
            }
            prepare.Disable(firstSystem);
            second.Values[0] = new Vector4(11, 12, 13, 14);
            Write();
            Assert.False(test.Backend.IsComplete(submitted[0]));
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            using (frame)
            {
                var commands = graph.BeginFrame();
                try
                {
                    frames.Update(frame, commands);
                    transfer.Stage(ref commands, first.Batch!, firstReader.View);
                    transfer.Stage(ref commands, second.Batch!, secondReader.View);
                    var copy = new InstanceReadback(transfer.Data, commands.Import(copiedData, []), checked((ulong)instances.PropertyDataBytes));
                    commands.AddCopyPass("Read both instance batches", PassQueueSelection.AnyOfType(QueueType.Graphics), copy,
                        new PassOptions(Culling: PassCullingMode.NeverCull),
                        static (ref PassDefinition pass, ref InstanceReadback data) =>
                        {
                            pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                            pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                        }, InstanceReadback.Record);
                    var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                    int count = commands.Execute(completions);
                    submitted.AddRange(completions.AsSpan(0, count));
                    frame.Complete(completions.AsSpan(0, count));
                }
                finally { commands.Dispose(); }
            }
            Release();
            foreach (QueueCompletion completion in submitted)
                Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using var result = test.Backend.Map(copiedData, MapType.Read, BufferRange.Whole);
            Assert.Equal(first.Values[0], MemoryMarshal.Read<Vector4>(result.Bytes[checked((int)Address(firstReader.View))..]));
            Assert.Equal(second.Values[0], MemoryMarshal.Read<Vector4>(result.Bytes[checked((int)Address(secondReader.View))..]));
        }
        finally
        {
            Release();
            foreach (QueueCompletion completion in submitted) test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(5));
            Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            instances.Dispose();
        }

        uint Address(RenderInstanceBatchView view)
        {
            using var metadata = test.Backend.Map(view.Metadata, MapType.Read, view.MetadataRange);
            return MemoryMarshal.Read<uint>(metadata.Bytes[(layout.Resolve(value).Descriptor.MetadataWordOffset * 4)..])
                & RenderInstanceLinearMetadata.AddressMask;
        }

        void Write()
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope) { prepare.Update(scope); scope.Commit(); }
        }

        void Release() => test.Backend.Submit(compute, new([], [], [], [], [new(gate, 1)]));
    }

    private readonly record struct InstanceReadback(GraphBufferId Source, GraphBufferId Destination, ulong Bytes)
    {
        internal static void Record(ref CopyPassCommandScope commands, in InstanceReadback data) =>
            commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0,
                commands.GetBuffer(data.Destination), 0, data.Bytes));
    }

    private sealed class Reader(Writer writer, RenderInstancePropertyLayout layout) : ISystem<RenderFrameSystemContext>
    {
        internal RenderInstanceBatchView View;
        public void OnCreate(ref RenderFrameSystemContext context) { }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using var instances = context.OpenInstances();
            View = instances.Bind(writer.Batch!, layout);
        }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
    }
}
