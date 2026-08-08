using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ReadOnlyQueryPacketJobTests
{
    [Fact]
    public void RowFiltersBecomeDenseContiguousPacketsWithDisjointOutputRanges()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity[] source = new Entity[6];
        for (int index = 0; index < source.Length; index++)
        {
            source[index] = world.CreateEntity(new PacketValue(index + 10));
            world.Add(source[index], new PacketGate());
        }
        world.Disable<PacketGate>(source[1]);
        world.Disable<PacketGate>(source[4]);

        QueryHandle query = world.Query(
            new QueryDefinitionBuilder()
                .Read<PacketValue>()
                .Read<PacketGate>()
                .Enabled<PacketGate>()
                .Build());
        int[] values = new int[4];
        Entity[] entities = new Entity[4];
        int[] packetStarts = new int[4];
        int[] packetCounts = new int[4];
        Array.Fill(packetStarts, -1);

        int written = -1;
        world.ExecuteReadSnapshot(query, cursor =>
        {
            using ReadOnlyQueryPacketPlan plan =
                ReadOnlyQueryPacketJobs.CreatePlan(cursor, rowsPerPacket: 2);
            Assert.Equal(4, plan.RowCount);
            var job = new CopyPacketJob(
                values,
                entities,
                packetStarts,
                packetCounts);
            JobResourceAccess[] outputs =
            [
                JobResourceAccess.Write(values),
                JobResourceAccess.Write(entities),
                JobResourceAccess.Write(packetStarts),
                JobResourceAccess.Write(packetCounts),
            ];
            written = plan.ExecuteParallel(
                in job,
                outputs);
        });

        Assert.Equal(4, written);
        Assert.Equal([10, 12, 13, 15], values);
        Assert.Equal([source[0], source[2], source[3], source[5]], entities);
        Assert.Equal([0, 1, 3, -1], packetStarts);
        Assert.Equal([1, 2, 1, 0], packetCounts);

        world.ReleaseQuery(query);
    }

    [Fact]
    public void PacketReadRejectsComponentsNotDeclaredByTheQuery()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity(new PacketValue(7));
        world.Add(entity, new UndeclaredValue(9));
        QueryHandle query = world.Query(
            new QueryDefinitionBuilder()
                .Read<PacketValue>()
                .Build());
        var job = new UndeclaredReadJob();

        world.ExecuteReadSnapshot(query, cursor =>
        {
            InvalidOperationException? error = null;
            try
            {
                _ = ReadOnlyQueryPacketJobs.ExecuteParallel(cursor, in job);
            }
            catch (InvalidOperationException caught)
            {
                error = caught;
            }
            Assert.NotNull(error);
            Assert.Contains("access", error.Message, StringComparison.OrdinalIgnoreCase);
        });

        world.ReleaseQuery(query);
    }

    [Fact]
    public void PreparedPlanReusesPacketsForSeveralPassesAndCommandRecording()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity[] entities = new Entity[5];
        for (int index = 0; index < entities.Length; index++)
            entities[index] = world.CreateEntity(new PacketValue(index + 20));

        QueryHandle query = world.Query(
            new QueryDefinitionBuilder()
                .Read<PacketValue>()
                .Build());
        int[] first = new int[entities.Length];
        int[] second = new int[entities.Length];
        JobCommandBuffer? commands = null;

        world.ExecuteReadSnapshot(query, cursor =>
        {
            ReadOnlyQueryPacketPlan plan =
                ReadOnlyQueryPacketJobs.CreatePlan(cursor, rowsPerPacket: 2);
            try
            {
                Assert.Equal(3, plan.PacketCount);
                Assert.Equal(entities.Length, plan.RowCount);
                Assert.Equal(cursor.LastSystemVersion, plan.LastSystemVersion);

                var firstJob = new CopyValuesJob(first);
                Assert.Equal(
                    entities.Length,
                    plan.ExecuteParallel(
                        in firstJob,
                        [JobResourceAccess.Write(first)]));

                var secondJob = new CopyValuesJob(second);
                Assert.Equal(
                    entities.Length,
                    plan.ExecuteParallel(
                        in secondJob,
                        [JobResourceAccess.Write(second)]));

                var commandJob = new AddPacketMarkerJob();
                commands = plan.RecordParallel(in commandJob);
                Assert.Equal(plan.PacketCount, commands.ProducerCount);
            }
            finally
            {
                plan.Dispose();
            }

            var rejectedJob = new CopyValuesJob(first);
            ObjectDisposedException? disposed = null;
            try
            {
                _ = plan.ExecuteParallel(in rejectedJob);
            }
            catch (ObjectDisposedException failure)
            {
                disposed = failure;
            }
            Assert.NotNull(disposed);
        });

        Assert.Equal([20, 21, 22, 23, 24], first);
        Assert.Equal(first, second);
        using (commands)
            commands!.Playback();
        for (int index = 0; index < entities.Length; index++)
            Assert.Equal(index + 20, world.Read<PacketMarker>(entities[index]).Value);

        world.ReleaseQuery(query);
    }

    private readonly struct CopyPacketJob(
        int[] values,
        Entity[] entities,
        int[] packetStarts,
        int[] packetCounts) : IReadOnlyQueryPacketJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            Assert.Equal(context.RowCount, packet.Count);
            packetStarts[context.PacketIndex] = context.OutputStart;
            packetCounts[context.PacketIndex] = context.RowCount;
            ReadOnlySpan<PacketValue> sourceValues = packet.Read<PacketValue>();
            for (int row = 0; row < packet.Count; row++)
            {
                int output = checked(context.OutputStart + row);
                values[output] = sourceValues[row].Value;
                entities[output] = packet.Entities[row];
            }
        }
    }

    private readonly struct UndeclaredReadJob : IReadOnlyQueryPacketJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet) =>
            _ = packet.Read<UndeclaredValue>();
    }

    private readonly struct CopyValuesJob(int[] destination) : IReadOnlyQueryPacketJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            ReadOnlySpan<PacketValue> values = packet.Read<PacketValue>();
            for (int row = 0; row < packet.Count; row++)
                destination[checked(context.OutputStart + row)] = values[row].Value;
        }
    }

    private readonly struct AddPacketMarkerJob : IReadOnlyQueryPacketCommandJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet,
            ref JobCommandWriter commands)
        {
            ReadOnlySpan<PacketValue> values = packet.Read<PacketValue>();
            for (int row = 0; row < packet.Count; row++)
                commands.Add(packet.Entities[row], new PacketMarker(values[row].Value));
        }
    }

    private readonly record struct PacketValue(int Value) : IComponent;

    private readonly record struct UndeclaredValue(int Value) : IComponent;

    private readonly record struct PacketMarker(int Value) : IComponent;

    private readonly struct PacketGate : IEnableableComponent;

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobRuntimeScope(int workerCount)
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = workerCount,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
        }
    }
}
