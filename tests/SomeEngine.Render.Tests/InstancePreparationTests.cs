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
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(129)]
    public void RepeatedWritesRestoreCanceledValuesAndPreserveUntouchedRows(int count)
    {
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var token = builder.Register<Vector4>("Instance preparation tests", new("test.instance.values"),
            new("test.float4", 16, 16, 16, 1));
        var layout = builder.Freeze();
        var property = layout.Resolve(token);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            layout, new RenderInstanceOptions { RowCapacity = count, BatchCapacity = 1 });
        var writer = new Writer(layout, property, count);
        var reader = new Reader(writer, layout);
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(writer);
        using var frames = new RenderFrameSystems(world, instances);
        frames.Add(reader);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(test.Backend, test.Device,
            [test.Backend.GetQueue(test.Device, QueueType.Graphics)]);
        try
        {
            Write();
            Vector4[] expected = writer.Values.ToArray();
            Assert.Equal(expected, Read());
            for (int iteration = 0; iteration < 4; iteration++)
            {
                writer.Values = Enumerable.Range(0, count).Select(row => new Vector4(iteration + 10, row, -row, 7)).ToArray();
                writer.Cancel = true;
                Write();
                Assert.Equal(expected, Read());
                writer.Cancel = false;
                writer.OneRow = true;
                Write();
                expected[count / 2] = writer.Values[count / 2];
                Assert.Equal(expected, Read());
                writer.OneRow = false;
            }
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

        Vector4[] Read()
        {
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            using (frame)
            using (var commands = graph.BeginFrame())
            {
                frames.Update(frame, commands);
                var view = reader.View;
                using var metadata = test.Backend.Map(view.Metadata, MapType.Read, view.MetadataRange);
                uint word = MemoryMarshal.Read<uint>(metadata.Bytes);
                Assert.NotEqual(0u, word & RenderInstanceLinearMetadata.PerInstanceBit);
                using var data = test.Backend.Map(view.PropertyData!, MapType.Read, BufferRange.Whole);
                return MemoryMarshal.Cast<byte, Vector4>(data.Bytes.Slice(
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

    private sealed class Writer(RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<Vector4> property, int count) : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal Vector4[] Values = Enumerable.Range(0, count).Select(row => new Vector4(1, row, 3, 4)).ToArray();
        internal bool Cancel;
        internal bool OneRow;
        internal bool FailAfterWrite;

        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var write = Batch is null ? context.AllocateBatch(layout, count) : context.RewriteBatch(Batch, layout);
            var all = write.OpenWrite(layout);
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
