using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

/// <summary>
/// Test-only assembly of the terminal instance-storage capabilities. Product pipelines use
/// RenderInstanceBatchBuilder; low-level storage tests use this helper so no single-batch query
/// adapter has to remain in the product API.
/// </summary>
internal static class RenderInstanceTestComposition
{
    internal static RenderInstanceBatch? Compose<TProducer>(
        RenderWorld world,
        QueryHandle query,
        RenderInstanceWriteScope write,
        RenderInstancePropertyLayout exactLayout,
        in TProducer producer)
        where TProducer : struct, IRenderInstanceProducer
    {
        var state = new RawComposition<TProducer>(write, exactLayout, producer);
        world.ExecuteReadSnapshot(
            query,
            ref state,
            static (QueryCursor cursor, ref RawComposition<TProducer> current) =>
                current.Execute(cursor));
        return state.Batch;
    }

    internal static RenderInstanceBatch? Compose<TProducer>(
        RenderInstanceStorageSystem instances,
        RenderPrepareScope scope,
        RenderInstanceWriteScope write,
        QueryHandle query,
        RenderInstancePropertyLayout exactLayout,
        in TProducer producer)
        where TProducer : struct, IRenderInstanceProducer
    {
        var state = new OwnedComposition<TProducer>(
            instances,
            scope,
            write,
            exactLayout,
            producer);
        instances.World.ExecuteReadSnapshot(
            query,
            ref state,
            static (QueryCursor cursor, ref OwnedComposition<TProducer> current) =>
                current.Execute(cursor));
        return state.Batch;
    }

    internal static bool Rewrite<TProducer>(
        RenderInstanceStorageSystem instances,
        RenderPrepareScope scope,
        RenderInstanceWriteScope write,
        QueryHandle query,
        RenderInstanceBatch batch,
        in TProducer producer)
        where TProducer : struct, IRenderInstanceProducer
    {
        var state = new OwnedRewrite<TProducer>(
            instances,
            scope,
            write,
            batch,
            producer);
        instances.World.ExecuteReadSnapshot(
            query,
            ref state,
            static (QueryCursor cursor, ref OwnedRewrite<TProducer> current) =>
                current.Execute(cursor));
        return state.Rewritten;
    }

    private struct RawComposition<TProducer>
        where TProducer : struct, IRenderInstanceProducer
    {
        private readonly RenderInstanceWriteScope _write;
        private readonly RenderInstancePropertyLayout _exactLayout;
        private readonly TProducer _producer;

        internal RawComposition(
            RenderInstanceWriteScope write,
            RenderInstancePropertyLayout exactLayout,
            TProducer producer)
        {
            _write = write;
            _exactLayout = exactLayout;
            _producer = producer;
            Batch = null;
        }

        internal RenderInstanceBatch? Batch { get; private set; }

        internal void Execute(QueryCursor cursor)
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(cursor);
            if (packets.RowCount == 0)
                return;

            using RenderInstanceBatchComposition composition =
                _write.BeginBatch(_exactLayout, packets.RowCount);
            RenderInstancePropertyLayout properties = RequireProperties(_producer);
            _producer.Bind(composition.OpenWrite(properties));
            WritePackets(packets, composition.OpenWrite(properties), in _producer);
            Batch = composition.Publish();
        }
    }

    private struct OwnedComposition<TProducer>
        where TProducer : struct, IRenderInstanceProducer
    {
        private readonly RenderInstanceStorageSystem _instances;
        private readonly RenderPrepareScope _scope;
        private readonly RenderInstanceWriteScope _write;
        private readonly RenderInstancePropertyLayout _exactLayout;
        private readonly TProducer _producer;

        internal OwnedComposition(
            RenderInstanceStorageSystem instances,
            RenderPrepareScope scope,
            RenderInstanceWriteScope write,
            RenderInstancePropertyLayout exactLayout,
            TProducer producer)
        {
            _instances = instances;
            _scope = scope;
            _write = write;
            _exactLayout = exactLayout;
            _producer = producer;
            Batch = null;
        }

        internal RenderInstanceBatch? Batch { get; private set; }

        internal void Execute(QueryCursor cursor)
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(cursor);
            if (packets.RowCount == 0)
                return;

            using RenderInstanceWriteHandle handle = _instances.AllocateBatch(
                _scope,
                _write,
                _exactLayout,
                packets.RowCount);
            RenderInstancePropertyLayout properties = RequireProperties(_producer);
            _producer.Bind(handle.OpenWrite(properties));
            WritePackets(packets, handle.OpenWrite(properties), in _producer);
            Batch = handle.Publish();
        }
    }

    private struct OwnedRewrite<TProducer>
        where TProducer : struct, IRenderInstanceProducer
    {
        private readonly RenderInstanceStorageSystem _instances;
        private readonly RenderPrepareScope _scope;
        private readonly RenderInstanceWriteScope _write;
        private readonly RenderInstanceBatch _batch;
        private readonly TProducer _producer;

        internal OwnedRewrite(
            RenderInstanceStorageSystem instances,
            RenderPrepareScope scope,
            RenderInstanceWriteScope write,
            RenderInstanceBatch batch,
            TProducer producer)
        {
            _instances = instances;
            _scope = scope;
            _write = write;
            _batch = batch;
            _producer = producer;
            Rewritten = false;
        }

        internal bool Rewritten { get; private set; }

        internal void Execute(QueryCursor cursor)
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(cursor);
            if (packets.RowCount != _batch.InstanceCount)
                return;

            RenderInstancePropertyLayout properties = RequireProperties(_producer);
            using RenderInstanceWriteHandle handle = _instances.RewriteBatch(
                _scope,
                _write,
                _batch,
                properties);
            _producer.Bind(handle.OpenWrite(properties));
            WritePackets(packets, handle.OpenWrite(properties), in _producer);
            _ = handle.Publish();
            Rewritten = true;
        }
    }

    private static RenderInstancePropertyLayout RequireProperties<TProducer>(TProducer producer)
        where TProducer : struct, IRenderInstanceProducer =>
        producer.Properties
        ?? throw new InvalidOperationException(
            "A render-instance producer must declare its properties.");

    private static void WritePackets<TProducer>(
        ReadOnlyQueryPacketPlan packets,
        RenderInstanceWriteSlice destination,
        in TProducer producer)
        where TProducer : struct, IRenderInstanceProducer
    {
        var job = new ProducerPacketJob<TProducer>(destination, producer);
        int written = packets.ExecuteParallel(in job);
        if (written != packets.RowCount)
        {
            throw new InvalidOperationException(
                "Render-instance query packet count changed inside one read snapshot.");
        }
    }

    private readonly struct ProducerPacketJob<TProducer>(
        RenderInstanceWriteSlice destination,
        TProducer producer) : IReadOnlyQueryPacketJob
        where TProducer : struct, IRenderInstanceProducer
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet) =>
            producer.Write(
                destination.Slice(context.OutputStart, context.RowCount),
                packet);
    }
}
