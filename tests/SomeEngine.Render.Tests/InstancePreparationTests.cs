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
    public void RewritingOneInstanceReusesItsBatchAndPreservesOtherGpuValues()
    {
        const int count = 2;
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var token = builder.Register<Vector4>("Instance values", new("test.instance.values"),
            new("test.float4", 16, 16, 16, 1));
        var layout = builder.Freeze();
        var property = layout.Resolve(token);
        using var instances = new RenderInstanceStorageSystem(test.Graph, world, layout,
            new RenderInstanceOptions { RowCapacity = count, BatchCapacity = 1 });
        using var prepare = new RenderPrepareSystems(world, instances, extraction);
        var writer = new Writer(layout, property, count);
        prepare.Add(writer);
        using var frames = new RenderFrameSystems(world, instances);
        var reader = new Reader(writer, layout);
        frames.Add(reader);

        prepare.Update();
        RenderInstanceBatch batch = writer.Batch!;
        Vector4[] expected = writer.Values.ToArray();
        Assert.Equal(expected, Read());
        writer.Row = 0;
        writer.Values[0] = new Vector4(10, 20, 30, 40);
        prepare.Update();
        expected[0] = writer.Values[0];
        Assert.Same(batch, writer.Batch);
        Assert.Equal(expected, Read());

        Vector4[] Read()
        {
            using RenderGraphFrame frame = test.Graph.BeginFrame();
            frames.Update(frame);
            var metadata = new BytesReceiver();
            var values = new BytesReceiver();
            frame.AddReadback(new GraphBufferSlice(reader.View.Metadata, reader.View.MetadataRange), metadata, "Read instance metadata");
            frame.AddReadback(new GraphBufferSlice(reader.View.PropertyData, BufferRange.Whole), values, "Read instance values");
            ulong identity = frame.FrameIdentity;
            frame.Execute();
            Assert.Equal(WaitStatus.Completed, test.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
            uint address = MemoryMarshal.Read<uint>(metadata.Bytes) & RenderInstanceLinearMetadata.AddressMask;
            return MemoryMarshal.Cast<byte, Vector4>(values.Bytes.AsSpan(checked((int)address), count * 16)).ToArray();
        }
    }

    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }

    private sealed class Writer(RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<Vector4> property, int count) : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal readonly Vector4[] Values = Enumerable.Range(0, count).Select(row => new Vector4(1, row, 3, 4)).ToArray();
        internal int? Row;
        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var write = Batch is null ? context.AllocateBatch(layout, count) : context.RewriteBatch(Batch, layout);
            var values = write.OpenWrite(layout);
            values.BindPerInstance(property);
            if (Row is { } row) values.Write(property, row, Values[row]);
            else values.Write(property, Values);
            Batch = write.Publish();
        }
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (Batch is { } batch) context.Retire(batch);
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
