using System.Diagnostics;
using System.Globalization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;

namespace SomeEngine.ECS.Benchmarks;

public struct BenchmarkJobPosition : SomeEngine.ECS.IComponent
{
    public int X;
    public int Y;
}

public struct BenchmarkJobVelocity : SomeEngine.ECS.IComponent
{
    public int X;
    public int Y;
}

public readonly record struct BenchmarkDurablePosition(int X, int Y) :
    SomeEngine.ECS.IComponent;

public struct BenchmarkDurablePositionCodec : ICanonicalComponentCodec<BenchmarkDurablePosition>
{
    public void Write(ref DataWriter writer, in BenchmarkDurablePosition value)
    {
        writer.WriteInt32(value.X);
        writer.WriteInt32(value.Y);
    }

    public void Read(ref DataReader reader, out BenchmarkDurablePosition value) =>
        value = new BenchmarkDurablePosition(reader.ReadInt32(), reader.ReadInt32());
}

public partial struct BenchmarkIntegrateJob : IJobEntity
{
    public void Execute(in BenchmarkJobVelocity velocity, ref BenchmarkJobPosition position)
    {
        position.X += velocity.X;
        position.Y += velocity.Y;
    }
}

internal static partial class EcsBenchmarkSuite
{
    private const int SharedBucketCount = 16;
    private const int IndexBucketCount = 64;

    private static readonly BenchmarkBufferElement[] InitialBufferValues =
    [
        new(1),
        new(2),
        new(3),
        new(4),
        new(5),
        new(6),
        new(7),
        new(8),
        new(9),
        new(10),
    ];

    private static readonly int[] JobComponents =
    [
        ComponentMetadata<BenchmarkJobPosition>.Id,
        ComponentMetadata<BenchmarkJobVelocity>.Id,
    ];

    private static readonly int[] DurablePositionComponents =
    [
        ComponentMetadata<BenchmarkDurablePosition>.Id,
    ];

    private static readonly SerializationTypeKey DurablePositionTypeKey = new(
        new Guid("4a652121-b074-4f0a-a537-ebba37a086df"),
        "SomeEngine.ECS.Benchmarks.BenchmarkDurablePosition",
        0x4DEBC5E549BA31E7UL);

    private static readonly int[] FilterComponents =
    [
        ComponentMetadata<FilterPosition>.Id,
        ComponentMetadata<FilterVisibility>.Id,
    ];

    private static readonly int[] StorageOwnerComponents =
    [
        ComponentMetadata<StorageIndex>.Id,
        ComponentMetadata<StorageBucket>.Id,
    ];

    private static readonly int[] StorageBufferComponents =
    [
        ComponentMetadata<StorageIndex>.Id,
        ComponentMetadata<StorageBucket>.Id,
        BufferComponents.Header<BenchmarkBufferElement>(),
        BufferComponents.Inline<BenchmarkBufferElement>(),
    ];

    private static readonly int[] StorageSparseComponents =
    [
        ComponentMetadata<StorageSparse>.Id,
    ];

    private static string FormatHash(params long[] values)
    {
        ulong hash = FnvOffsetBasis;
        for (int i = 0; i < values.Length; i++)
            hash = Mix(hash, unchecked((ulong)values[i]));
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static string HashBytes(MemoryStream stream)
    {
        if (!stream.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
            throw new InvalidOperationException("Benchmark MemoryStream did not expose its buffer.");

        ulong hash = FnvOffsetBasis;
        ReadOnlySpan<byte> bytes = segment.Array.AsSpan(segment.Offset, checked((int)stream.Length));
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static World CreatePopulatedWorldWithEntities(
        int entityCount,
        int capturedEntityCount,
        out Entity[] entities)
    {
        if ((uint)capturedEntityCount > (uint)entityCount)
            throw new ArgumentOutOfRangeException(nameof(capturedEntityCount));
        var world = new World(entityCount);
        entities = new Entity[capturedEntityCount];
        var state = new EntityCaptureState(world, entities, entityCount);
        world.ExecuteBundleSpawnBatch(
            PositionComponents,
            state.EntityCount,
            ref state,
            static (BundleWriteView view, ref EntityCaptureState capture) =>
            {
                var position = new Position(view.Index, view.Index + 1);
                view.Write(in position);
                if (view.Index < capture.Entities.Length)
                    capture.Entities[view.Index] = view.Entity;
            });
        return world;
    }

    private sealed class ParallelIntegrateExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;

        internal ParallelIntegrateExecution(int entityCount)
        {
            _entityCount = entityCount;
            World = new World(entityCount);
            World.ExecuteBundleSpawnBatch(
                JobComponents,
                entityCount,
                static view =>
                {
                    var position = new BenchmarkJobPosition
                    {
                        X = view.Index,
                        Y = -view.Index,
                    };
                    var velocity = new BenchmarkJobVelocity
                    {
                        X = view.Index % 7 + 1,
                        Y = 2,
                    };
                    view.Write(in position);
                    view.Write(in velocity);
                });
        }

        public World World { get; }

        public void Execute() =>
            new BenchmarkIntegrateJob()
                .ScheduleParallel(
                    World,
                    new JobEntityScheduleOptions(rowsPerPacket: 256))
                .Complete();

        public string ValidateAndGetChecksum()
        {
            QueryHandle query = World.Query(
                World.QueryDefinition()
                    .Read<BenchmarkJobPosition>()
                    .Read<BenchmarkJobVelocity>());
            var state = new JobChecksum();
            World.ExecuteQuery(
                query,
                ref state,
                static (QueryCursor cursor, ref JobChecksum checksum) =>
                {
                    foreach (QueryRow row in cursor.Rows)
                    {
                        BenchmarkJobPosition position = row.Read<BenchmarkJobPosition>();
                        BenchmarkJobVelocity velocity = row.Read<BenchmarkJobVelocity>();
                        checksum.Count++;
                        checksum.SumX += position.X;
                        checksum.SumY += position.Y;
                        checksum.VelocitySum += velocity.X + velocity.Y;
                    }
                });

            long initialSum = (long)_entityCount * (_entityCount - 1) / 2;
            long velocityX = 0;
            for (int i = 0; i < _entityCount; i++)
                velocityX += i % 7 + 1;
            long expectedX = initialSum + velocityX;
            long expectedY = -initialSum + 2L * _entityCount;
            if (state.Count != _entityCount || state.SumX != expectedX || state.SumY != expectedY)
            {
                throw new InvalidOperationException(
                    "Parallel Job benchmark did not integrate every entity exactly once.");
            }
            return FormatHash(state.Count, state.SumX, state.SumY, state.VelocitySum);
        }
    }

    private sealed class ChangedEnabledExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly int _queryIterations;
        private readonly QueryHandle _query;
        private readonly uint _lastVersion;
        private readonly long _expectedRowsPerIteration;
        private readonly long _expectedSumPerIteration;
        private long _rows;
        private long _sum;

        internal ChangedEnabledExecution(int entityCount, int queryIterations)
        {
            _entityCount = entityCount;
            _queryIterations = queryIterations;
            World = new World(entityCount);
            var entities = new Entity[entityCount];
            var capture = new FilterPopulateState(entities);
            World.ExecuteBundleSpawnBatch(
                FilterComponents,
                entityCount,
                ref capture,
                static (BundleWriteView view, ref FilterPopulateState state) =>
                {
                    var position = new FilterPosition(view.Index);
                    var visibility = new FilterVisibility(view.Index);
                    view.Write(in position);
                    view.Write(in visibility);
                    state.Entities[view.Index] = view.Entity;
                });

            for (int i = 1; i < entities.Length; i += 2)
                World.Disable<FilterVisibility>(entities[i]);

            _lastVersion = World.AcquireSystemTick();
            long expectedRows = 0;
            long expectedSum = 0;
            for (int i = 0; i < entities.Length; i += 4)
            {
                int value = checked(i + 1_000);
                World.Replace(entities[i], new FilterPosition(value));
                expectedRows++;
                expectedSum += value;
            }
            _expectedRowsPerIteration = expectedRows;
            _expectedSumPerIteration = expectedSum;
            _query = World.Query(
                World.QueryDefinition()
                    .Read<FilterPosition>()
                    .Read<FilterVisibility>()
                    .Changed<FilterPosition>()
                    .Enabled<FilterVisibility>());
        }

        public World World { get; }

        public void Execute()
        {
            var state = new FilterChecksum();
            for (int iteration = 0; iteration < _queryIterations; iteration++)
            {
                World.ExecuteQuery(
                    _query,
                    _lastVersion,
                    ref state,
                    static (QueryCursor cursor, ref FilterChecksum checksum) =>
                    {
                        foreach (QueryRow row in cursor.Rows)
                        {
                            checksum.Rows++;
                            checksum.Sum += row.Read<FilterPosition>().Value;
                        }
                    });
            }
            _rows = state.Rows;
            _sum = state.Sum;
        }

        public string ValidateAndGetChecksum()
        {
            long expectedRows = checked(_expectedRowsPerIteration * _queryIterations);
            long expectedSum = checked(_expectedSumPerIteration * _queryIterations);
            if (_rows != expectedRows || _sum != expectedSum || World.EntityCount != _entityCount)
            {
                throw new InvalidOperationException(
                    "Changed+Enabled benchmark selected an incorrect row set.");
            }
            return FormatHash(_rows, _sum, World.CurrentTick);
        }
    }

    private sealed class StorageOwnersExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly int _bufferEntityCount;
        private readonly QueryHandle _bufferQuery;
        private readonly QueryHandle _sharedQuery;
        private StorageChecksum _checksum;

        internal StorageOwnersExecution(int entityCount)
        {
            _entityCount = entityCount;
            _bufferEntityCount = Math.Min(entityCount, 4_096);
            World = new World(entityCount);
            var bufferState = new StoragePopulateState(offset: 0, includeBuffer: true);
            World.ExecuteBundleSpawnBatch(
                StorageBufferComponents,
                StorageSparseComponents,
                _bufferEntityCount,
                ref bufferState,
                static (BundleWriteView view, ref StoragePopulateState state) =>
                {
                    int item = state.Offset + view.Index;
                    var index = new StorageIndex(item % IndexBucketCount);
                    var bucket = new StorageBucket(item % SharedBucketCount);
                    var sparse = new StorageSparse(item);
                    view.WriteShared(in bucket);
                    view.Write(in index);
                    view.WriteSparse(in sparse);
                    if (state.IncludeBuffer)
                    {
                        ReadOnlyMemory<BenchmarkBufferElement> buffer =
                            InitialBufferValues.AsMemory();
                        view.WriteBuffer(in buffer);
                    }
                });
            var ownerState = new StoragePopulateState(_bufferEntityCount, includeBuffer: false);
            World.ExecuteBundleSpawnBatch(
                StorageOwnerComponents,
                StorageSparseComponents,
                entityCount - _bufferEntityCount,
                ref ownerState,
                static (BundleWriteView view, ref StoragePopulateState state) =>
                {
                    int item = state.Offset + view.Index;
                    var index = new StorageIndex(item % IndexBucketCount);
                    var bucket = new StorageBucket(item % SharedBucketCount);
                    var sparse = new StorageSparse(item);
                    view.WriteShared(in bucket);
                    view.Write(in index);
                    view.WriteSparse(in sparse);
                });
            _bufferQuery = World.Query(World.QueryDefinition().ReadBuffer<BenchmarkBufferElement>());
            _sharedQuery = World.Query(World.QueryDefinition().Shared<StorageBucket>());
        }

        public World World { get; }

        public void Execute()
        {
            var checksum = new StorageChecksum();
            World.ExecuteSparseWrite<StorageSparse, StorageChecksum>(
                ref checksum,
                static (ReadOnlySpan<Entity> entities, Span<StorageSparse> values, ref StorageChecksum state) =>
                {
                    state.SparseCount = entities.Length;
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = new StorageSparse(values[i].Value + 1);
                        state.SparseSum += values[i].Value;
                    }
                });

            World.ExecuteQuery(
                _bufferQuery,
                ref checksum,
                static (QueryCursor cursor, ref StorageChecksum state) =>
                {
                    foreach (QueryRow row in cursor.Rows)
                    {
                        BufferView<BenchmarkBufferElement> buffer = row.ReadBuffer<BenchmarkBufferElement>();
                        state.BufferElements += buffer.Count;
                        for (int i = 0; i < buffer.Count; i++)
                            state.BufferSum += buffer[i].Value;
                    }
                });

            for (int bucket = 0; bucket < SharedBucketCount; bucket++)
            {
                var value = new StorageBucket(bucket);
                var sharedState = new SharedCountState(value);
                World.ExecuteQuery(
                    _sharedQuery,
                    ref sharedState,
                    static (QueryCursor cursor, ref SharedCountState state) =>
                    {
                        foreach (QueryRow _ in cursor.RowsWithShared(in state.Bucket))
                            state.Count++;
                    });
                checksum.SharedRows += sharedState.Count;
            }

            for (int key = 0; key < IndexBucketCount; key++)
                checksum.IndexRows += World.GetByIndex<StorageIndex, int>(key).Length;
            _checksum = checksum;
        }

        public string ValidateAndGetChecksum()
        {
            long expectedSparseSum = (long)_entityCount * (_entityCount - 1) / 2 + _entityCount;
            long expectedBufferElements = checked((long)_bufferEntityCount * InitialBufferValues.Length);
            long expectedBufferSum = checked((long)_bufferEntityCount * 55);
            if (_checksum.SparseCount != _entityCount ||
                _checksum.SparseSum != expectedSparseSum ||
                _checksum.BufferElements != expectedBufferElements ||
                _checksum.BufferSum != expectedBufferSum ||
                _checksum.SharedRows != _entityCount ||
                _checksum.IndexRows != _entityCount)
            {
                throw new InvalidOperationException(
                    "Storage-owner benchmark failed buffer/sparse/shared/index validation.");
            }
            return FormatHash(
                _checksum.SparseCount,
                _checksum.SparseSum,
                _checksum.BufferElements,
                _checksum.BufferSum,
                _checksum.SharedRows,
                _checksum.IndexRows);
        }
    }

    private sealed class RelationMaintenanceExecution : IBenchmarkExecution
    {
        private readonly Entity _source;
        private readonly Entity[] _targets;
        private readonly RelationEdge<BenchmarkRelation>[] _edges;

        internal RelationMaintenanceExecution(int topologyCount)
        {
            World = new World(checked(topologyCount * 2));
            _source = World.CreateEntity();
            _targets = new Entity[Math.Max(0, topologyCount - 1)];
            _edges = new RelationEdge<BenchmarkRelation>[_targets.Length];
            for (int i = 0; i < _targets.Length; i++)
            {
                _targets[i] = World.CreateEntity();
                _edges[i] = World.CreateRelation(
                    _source,
                    _targets[i],
                    new BenchmarkRelation(i));
            }
        }

        public World World { get; }

        public void Execute()
        {
            if (_edges.Length == 0)
                return;

            using var commands = new CommandBuffer(World);
            RelationCommandWriter<BenchmarkRelation> relations = commands.Relations<BenchmarkRelation>();
            for (int i = 0; i < _edges.Length; i++)
            {
                relations.Retarget(
                    _edges[i],
                    _source,
                    _targets[(i + 1) % _targets.Length],
                    RelationMaintenanceTiming.Deferred);
            }
            commands.Playback();
            World.MaintainRelations<BenchmarkRelation>();
        }

        public string ValidateAndGetChecksum()
        {
            RelationAdjacencySnapshot<BenchmarkRelation> outgoing =
                World.GetOutgoingRelations<BenchmarkRelation>(_source);
            if (outgoing.Count != _edges.Length)
                throw new InvalidOperationException("Relation benchmark lost outgoing adjacency.");

            long endpointSum = 0;
            long payloadSum = 0;
            for (int i = 0; i < _edges.Length; i++)
            {
                DirectedRelationEndpoints<BenchmarkRelation> endpoints =
                    World.GetDirectedRelationEndpoints(_edges[i]);
                Entity expectedTarget = _targets[(i + 1) % _targets.Length];
                if (endpoints.Source != _source || endpoints.Target != expectedTarget)
                    throw new InvalidOperationException("Relation benchmark published incorrect endpoints.");
                endpointSum += endpoints.Target.Index;
                payloadSum += World.Read<BenchmarkRelation>(_edges[i].Entity).Value;
            }
            return FormatHash(_edges.Length, outgoing.Generation, endpointSum, payloadSum);
        }
    }

    private sealed class HierarchyMaintenanceExecution : IBenchmarkExecution
    {
        private readonly Entity[] _nodes;

        internal HierarchyMaintenanceExecution(int topologyCount)
        {
            World = new World(topologyCount);
            _nodes = new Entity[topologyCount];
            for (int i = 0; i < _nodes.Length; i++)
                _nodes[i] = World.CreateEntity();

            if (_nodes.Length <= 1)
                return;

            using var commands = new CommandBuffer(World);
            HierarchyCommandWriter<BenchmarkHierarchyDomain> hierarchy =
                commands.Hierarchy<BenchmarkHierarchyDomain>();
            for (int i = 1; i < _nodes.Length; i++)
            {
                hierarchy.SetParent(
                    _nodes[i],
                    _nodes[i - 1],
                    HierarchyMaintenanceTiming.Deferred);
            }
            commands.Playback();
            Hierarchy<BenchmarkHierarchyDomain>.Maintain(World);
        }

        public World World { get; }

        public void Execute()
        {
            if (_nodes.Length <= 1)
                return;

            using var commands = new CommandBuffer(World);
            HierarchyCommandWriter<BenchmarkHierarchyDomain> hierarchy =
                commands.Hierarchy<BenchmarkHierarchyDomain>();
            for (int i = 1; i < _nodes.Length; i++)
            {
                hierarchy.SetParent(
                    _nodes[i],
                    _nodes[0],
                    HierarchyMaintenanceTiming.Deferred);
            }
            commands.Playback();
            Hierarchy<BenchmarkHierarchyDomain>.Maintain(World);
        }

        public string ValidateAndGetChecksum()
        {
            if (_nodes.Length == 0)
                throw new InvalidOperationException("Hierarchy benchmark requires at least one node.");

            HierarchyChildrenSnapshot<BenchmarkHierarchyDomain> children =
                Hierarchy<BenchmarkHierarchyDomain>.GetChildren(World, _nodes[0]);
            if (children.Count != _nodes.Length - 1)
                throw new InvalidOperationException("Hierarchy benchmark did not publish the expected fanout.");

            long sum = 0;
            for (int i = 1; i < _nodes.Length; i++)
            {
                if (Hierarchy<BenchmarkHierarchyDomain>.GetParent(World, _nodes[i]) != _nodes[0])
                    throw new InvalidOperationException("Hierarchy benchmark published an incorrect parent.");
                sum += _nodes[i].Index;
            }
            return FormatHash(_nodes.Length, unchecked((long)children.Generation), sum);
        }
    }

    private sealed class CommandBufferChurnExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly int _width;
        private readonly int _iterations;
        private readonly Entity[] _entities;
        private readonly WorldStructuralMetrics _before;

        internal CommandBufferChurnExecution(int entityCount, int width, int iterations)
        {
            _entityCount = entityCount;
            _width = width;
            _iterations = iterations;
            World = CreatePopulatedWorldWithEntities(entityCount, width, out _entities);
            _before = World.GetStructuralMetrics();
        }

        public World World { get; }

        public void Execute()
        {
            for (int iteration = 0; iteration < _iterations; iteration++)
            {
                using var commands = new CommandBuffer(World);
                for (int i = 0; i < _width; i++)
                {
                    if ((iteration & 1) == 0)
                        commands.AddTag<ChurnTag>(_entities[i]);
                    else
                        commands.RemoveTag<ChurnTag>(_entities[i]);
                }
                commands.Playback();
            }
        }

        public string ValidateAndGetChecksum()
        {
            WorldStructuralMetrics after = World.GetStructuralMetrics();
            if (after.Published - _before.Published != _iterations ||
                after.Started - _before.Started != _iterations ||
                after.Aborted != _before.Aborted)
            {
                throw new InvalidOperationException(
                    "Command-buffer churn must publish exactly one candidate per playback.");
            }

            bool expectedTag = (_iterations & 1) != 0;
            for (int i = 0; i < _entities.Length; i++)
            {
                bool expected = i < _width && expectedTag;
                if (World.Has<ChurnTag>(_entities[i]) != expected)
                    throw new InvalidOperationException("Command-buffer churn produced an incorrect tag set.");
            }
            WorldChecksum checksum = ValidateWorld(World, _entityCount, addedEntityCount: 0);
            return FormatHash(
                checksum.Count,
                checksum.SumX,
                checksum.SumY,
                unchecked((long)checksum.Hash),
                after.Published - _before.Published);
        }
    }

    private sealed class SnapshotWriteExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly SerializationRegistry _registry = new SerializationRegistry().Register<Position>();
        private MemoryStream? _stream;

        internal SnapshotWriteExecution(int entityCount)
        {
            _entityCount = entityCount;
            World = CreatePopulatedWorld(entityCount);
        }

        public World World { get; }

        public BenchmarkWorkloadMetricSample WorkloadMetrics { get; private set; } =
            BenchmarkWorkloadMetricSample.Empty;

        public void Execute()
        {
            _stream = new MemoryStream();
            long started = Stopwatch.GetTimestamp();
            WorldSerializer.WriteWorld(_stream, World, _registry);
            WorkloadMetrics = BenchmarkWorkloadMetricSample.Empty with
            {
                PayloadBytes = _stream.Length,
                SnapshotWriteMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };
        }

        public string ValidateAndGetChecksum()
        {
            MemoryStream stream = _stream ??
                throw new InvalidOperationException("Snapshot benchmark did not produce a stream.");
            if (stream.Length <= 0)
                throw new InvalidOperationException("Snapshot benchmark produced an empty payload.");

            string hash = HashBytes(stream);
            stream.Position = 0;
            using World loaded = WorldSerializer.ReadWorld(stream, _registry);
            _ = ValidateWorld(loaded, _entityCount, addedEntityCount: 0);
            return hash;
        }
    }

    private sealed class SnapshotReadExecution : IBenchmarkExecution, IDisposable
    {
        private readonly int _entityCount;
        private readonly SerializationRegistry _registry = new SerializationRegistry().Register<Position>();
        private readonly MemoryStream _payload;
        private readonly string _payloadHash;
        private World? _loaded;

        internal SnapshotReadExecution(int entityCount)
        {
            _entityCount = entityCount;
            World = CreatePopulatedWorld(entityCount);
            _payload = new MemoryStream();
            WorldSerializer.WriteWorld(_payload, World, _registry);
            _payloadHash = HashBytes(_payload);
        }

        public World World { get; }

        public BenchmarkWorkloadMetricSample WorkloadMetrics { get; private set; } =
            BenchmarkWorkloadMetricSample.Empty;

        public void Execute()
        {
            _payload.Position = 0;
            long started = Stopwatch.GetTimestamp();
            _loaded = WorldSerializer.ReadWorld(_payload, _registry);
            WorkloadMetrics = BenchmarkWorkloadMetricSample.Empty with
            {
                PayloadBytes = _payload.Length,
                LoadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };
        }

        public string ValidateAndGetChecksum()
        {
            World loaded = _loaded ??
                throw new InvalidOperationException("Snapshot-read benchmark did not load a World.");
            _ = ValidateWorld(loaded, _entityCount, addedEntityCount: 0);
            return _payloadHash;
        }

        public void Dispose()
        {
            _loaded?.Dispose();
            _loaded = null;
            _payload.Dispose();
        }
    }

    private sealed class MixedFrameExecution : IBenchmarkExecution, IDisposable
    {
        private readonly int _entityCount;
        private readonly SerializationRegistry _registry = new SerializationRegistry()
            .Register<BenchmarkJobPosition>()
            .Register<BenchmarkJobVelocity>();
        private MemoryStream? _stream;
        private World? _loaded;

        internal MixedFrameExecution(int entityCount)
        {
            _entityCount = entityCount;
            World = new World(entityCount);
            World.ExecuteBundleSpawnBatch(
                JobComponents,
                entityCount,
                static view =>
                {
                    var position = new BenchmarkJobPosition
                    {
                        X = view.Index,
                        Y = -view.Index,
                    };
                    var velocity = new BenchmarkJobVelocity
                    {
                        X = view.Index % 7 + 1,
                        Y = 2,
                    };
                    view.Write(in position);
                    view.Write(in velocity);
                });
        }

        public World World { get; }

        public BenchmarkWorkloadMetricSample WorkloadMetrics { get; private set; } =
            BenchmarkWorkloadMetricSample.Empty;

        public void Execute()
        {
            long updateStarted = Stopwatch.GetTimestamp();
            new BenchmarkIntegrateJob()
                .ScheduleParallel(
                    World,
                    new JobEntityScheduleOptions(rowsPerPacket: 256))
                .Complete();
            double updateMilliseconds = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;

            _stream = new MemoryStream();
            long writeStarted = Stopwatch.GetTimestamp();
            WorldSerializer.WriteWorld(_stream, World, _registry);
            double writeMilliseconds = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds;

            _stream.Position = 0;
            long loadStarted = Stopwatch.GetTimestamp();
            _loaded = WorldSerializer.ReadWorld(_stream, _registry);
            double loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;

            WorkloadMetrics = new BenchmarkWorkloadMetricSample(
                _stream.Length,
                updateMilliseconds,
                writeMilliseconds,
                loadMilliseconds,
                DurableCommitMilliseconds: 0,
                DurableLoadMilliseconds: 0);
        }

        public string ValidateAndGetChecksum()
        {
            MemoryStream stream = _stream ??
                throw new InvalidOperationException("Mixed-frame benchmark produced no payload.");
            World loaded = _loaded ??
                throw new InvalidOperationException("Mixed-frame benchmark did not load a World.");
            ValidateIntegratedJobWorld(World, _entityCount);
            ValidateIntegratedJobWorld(loaded, _entityCount);
            return HashBytes(stream);
        }

        public void Dispose()
        {
            _loaded?.Dispose();
            _loaded = null;
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed class DurableSaveRoundTripExecution : IBenchmarkExecution, IDisposable
    {
        private readonly int _entityCount;
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(),
            "SomeEngine.ECS.Benchmarks",
            Guid.NewGuid().ToString("N"));
        private readonly SerializationRegistry _registry;
        private readonly DurableSaveStore _store;
        private DurableSaveCommit? _commit;
        private World? _loaded;

        internal DurableSaveRoundTripExecution(int entityCount)
        {
            _entityCount = entityCount;
            _registry = new SerializationRegistry().RegisterCanonical<
                BenchmarkDurablePosition,
                BenchmarkDurablePositionCodec>(DurablePositionTypeKey);
            World = new World(entityCount);
            World.ExecuteBundleSpawnBatch(
                DurablePositionComponents,
                entityCount,
                static view =>
                {
                    var position = new BenchmarkDurablePosition(view.Index, view.Index + 1);
                    view.Write(in position);
                });
            _store = new DurableSaveStore(Path.Combine(_directoryPath, "world.save"));
        }

        public World World { get; }

        public BenchmarkWorkloadMetricSample WorkloadMetrics { get; private set; } =
            BenchmarkWorkloadMetricSample.Empty;

        public void Execute()
        {
            long commitStarted = Stopwatch.GetTimestamp();
            _commit = _store.WriteWorld(World, _registry);
            double commitMilliseconds = Stopwatch.GetElapsedTime(commitStarted).TotalMilliseconds;

            long loadStarted = Stopwatch.GetTimestamp();
            _loaded = _store.ReadWorld(_registry);
            double loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;

            WorkloadMetrics = BenchmarkWorkloadMetricSample.Empty with
            {
                PayloadBytes = _commit.PayloadLength,
                DurableCommitMilliseconds = commitMilliseconds,
                DurableLoadMilliseconds = loadMilliseconds,
            };
        }

        public string ValidateAndGetChecksum()
        {
            DurableSaveCommit commit = _commit ??
                throw new InvalidOperationException("Durable-save benchmark did not commit a generation.");
            World loaded = _loaded ??
                throw new InvalidOperationException("Durable-save benchmark did not reload a World.");
            DurablePositionChecksum checksum = ValidateDurableWorld(loaded, _entityCount);
            return FormatHash(
                unchecked((long)commit.Generation),
                commit.PayloadLength,
                checksum.Count,
                checksum.SumX,
                checksum.SumY);
        }

        public void Dispose()
        {
            _loaded?.Dispose();
            _loaded = null;
            _store.Dispose();
            try
            {
                if (Directory.Exists(_directoryPath))
                    Directory.Delete(_directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateIntegratedJobWorld(World world, int entityCount)
    {
        QueryHandle query = world.Query(
            world.QueryDefinition()
                .Read<BenchmarkJobPosition>()
                .Read<BenchmarkJobVelocity>());
        var state = new JobChecksum();
        world.ExecuteQuery(
            query,
            ref state,
            static (QueryCursor cursor, ref JobChecksum checksum) =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    BenchmarkJobPosition position = row.Read<BenchmarkJobPosition>();
                    BenchmarkJobVelocity velocity = row.Read<BenchmarkJobVelocity>();
                    checksum.Count++;
                    checksum.SumX += position.X;
                    checksum.SumY += position.Y;
                    checksum.VelocitySum += velocity.X + velocity.Y;
                }
            });

        long initialSum = (long)entityCount * (entityCount - 1) / 2;
        long velocityX = 0;
        for (int i = 0; i < entityCount; i++)
            velocityX += i % 7 + 1;
        if (state.Count != entityCount ||
            state.SumX != initialSum + velocityX ||
            state.SumY != -initialSum + 2L * entityCount)
        {
            throw new InvalidOperationException(
                "Mixed-frame benchmark did not preserve the integrated Job state.");
        }
    }

    private static DurablePositionChecksum ValidateDurableWorld(World world, int entityCount)
    {
        QueryHandle query = world.Query(
            world.QueryDefinition().Read<BenchmarkDurablePosition>());
        var checksum = new DurablePositionChecksum();
        world.ExecuteQuery(
            query,
            ref checksum,
            static (QueryCursor cursor, ref DurablePositionChecksum state) =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    BenchmarkDurablePosition position = row.Read<BenchmarkDurablePosition>();
                    state.Count++;
                    state.SumX += position.X;
                    state.SumY += position.Y;
                }
            });

        long expectedSumX = (long)entityCount * (entityCount - 1) / 2;
        long expectedSumY = (long)entityCount * (entityCount + 1L) / 2;
        if (world.EntityCount != entityCount ||
            checksum.Count != entityCount ||
            checksum.SumX != expectedSumX ||
            checksum.SumY != expectedSumY)
        {
            throw new InvalidOperationException(
                "Durable-save benchmark did not reload the committed entity state.");
        }
        return checksum;
    }

    private struct JobChecksum
    {
        internal long Count;
        internal long SumX;
        internal long SumY;
        internal long VelocitySum;
    }

    private struct DurablePositionChecksum
    {
        internal long Count;
        internal long SumX;
        internal long SumY;
    }

    private struct FilterChecksum
    {
        internal long Rows;
        internal long Sum;
    }

    private struct StorageChecksum
    {
        internal long SparseCount;
        internal long SparseSum;
        internal long BufferElements;
        internal long BufferSum;
        internal long SharedRows;
        internal long IndexRows;
    }

    private struct SharedCountState
    {
        internal SharedCountState(StorageBucket bucket)
        {
            Bucket = bucket;
        }

        internal StorageBucket Bucket;
        internal long Count;
    }

    private readonly struct EntityCaptureState
    {
        internal EntityCaptureState(World world, Entity[] entities, int entityCount)
        {
            World = world;
            Entities = entities;
            EntityCount = entityCount;
        }

        internal World World { get; }
        internal Entity[] Entities { get; }
        internal int EntityCount { get; }
    }

    private readonly struct FilterPopulateState
    {
        internal FilterPopulateState(Entity[] entities) => Entities = entities;

        internal Entity[] Entities { get; }
    }

    private readonly struct StoragePopulateState
    {
        internal StoragePopulateState(int offset, bool includeBuffer)
        {
            Offset = offset;
            IncludeBuffer = includeBuffer;
        }

        internal int Offset { get; }
        internal bool IncludeBuffer { get; }
    }

    private readonly record struct FilterPosition(int Value) : SomeEngine.ECS.IComponent;

    private readonly record struct FilterVisibility(int Value) : SomeEngine.ECS.IEnableableComponent;

    private readonly record struct StorageSparse(int Value) : SomeEngine.ECS.Components.ISparseComponent;

    private readonly record struct StorageBucket(int Value) : SomeEngine.ECS.Components.ISharedComponent;

    private readonly record struct StorageIndex(int Key) : SomeEngine.ECS.Components.IIndexedComponent<int>
    {
        public int GetKey() => Key;
    }

    [BufferCapacity(8)]
    private readonly record struct BenchmarkBufferElement(int Value) : SomeEngine.ECS.Components.IBufferElement;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly record struct BenchmarkRelation(int Value) : SomeEngine.ECS.IComponent;

    private readonly struct BenchmarkHierarchyDomain : IHierarchyDomain;

    private readonly struct ChurnTag : SomeEngine.ECS.Components.ITag;
}
