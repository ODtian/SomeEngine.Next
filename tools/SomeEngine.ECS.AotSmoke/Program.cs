using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using TableComponent = SomeEngine.ECS.IComponent;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class Program
{
    public static int Main()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
            SafetyMode = JobSafetyMode.Checked,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        try
        {
            AotSmokeResult result = Run();
            Console.WriteLine(JsonSerializer.Serialize(
                result,
                AotSmokeJsonContext.Default.AotSmokeResult));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            JobSystem.Shutdown();
        }
    }

    private static AotSmokeResult Run()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        registry
            .RegisterHierarchyDomain<AotHierarchyDomain>()
            .RegisterRelationTopology<AotLink>();

        SerializationTypeEntry positionEntry = registry.Entries.ToArray().Single(
            entry => entry.RuntimeComponentId == ComponentMetadata<AotPosition>.Id);
        Require(
            positionEntry.CodecKind == ComponentCodecKind.RawCanonical,
            "The packed AOT component did not retain its RawCanonical layout proof.");

        JobRuntimeStats beforeRejectedJob = JobSystem.GetStats();
        bool asyncJobRejected = false;
        try
        {
            JobSystem.Schedule(new AotAsyncVoidJob()).Complete();
        }
        catch (InvalidOperationException)
        {
            asyncJobRejected = true;
        }
        Require(asyncJobRejected, "NativeAOT accepted an async-void IJob callback.");
        Require(AotAsyncVoidJob.BodyCount == 0, "Rejected async-void job executed its body.");
        JobRuntimeStats afterRejectedJob = JobSystem.GetStats();
        Require(
            beforeRejectedJob.ScheduledJobs == afterRejectedJob.ScheduledJobs &&
            beforeRejectedJob.CompletedHandles == afterRejectedJob.CompletedHandles,
            "Rejected async-void job mutated scheduler statistics.");

        AotPosition rootInitial = new()
        {
            Marker = 0xA5,
            Value = unchecked((int)0x10203040),
            Scale = BitConverter.Int32BitsToSingle(unchecked((int)0x3F123456)),
            Stamp = unchecked((long)0x0123456789ABCDEF),
        };
        AotPosition childInitial = new()
        {
            Marker = 0x5A,
            Value = unchecked((int)0x89ABCDEF),
            Scale = BitConverter.Int32BitsToSingle(unchecked((int)0xBF654321)),
            Stamp = unchecked((long)0xFEDCBA9876543210),
        };
        AotPosition deferredInitial = new()
        {
            Marker = 0xC3,
            Value = unchecked((int)0x55AA33CC),
            Scale = BitConverter.Int32BitsToSingle(unchecked((int)0x41234567)),
            Stamp = unchecked((long)0x13579BDF2468ACE0),
        };

        var source = new World();
        Entity root = source.CreateEntity(rootInitial);
        source.Add(root, new AotVelocity { Value = 2 });
        Entity child = source.CreateEntity(childInitial);
        source.Add(child, new AotVelocity { Value = 3 });

        using (var commands = new CommandBuffer(source))
        {
            DeferredEntity deferred = commands.CreateEntity();
            commands.Add(deferred, deferredInitial);
            commands.Add(deferred, new AotVelocity { Value = 4 });
            commands.Playback();
        }

        Hierarchy<AotHierarchyDomain>.SetParent(source, child, root);
        RelationEdge<AotLink> edge = source.CreateRelation(
            root,
            child,
            new AotLink { Weight = 7 });

        new AotIntegrateJob().ScheduleParallel(
            source,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();

        AotPosition rootExpected = rootInitial;
        rootExpected.Value += 2;
        AotPosition childExpected = childInitial;
        childExpected.Value += 3;
        RequirePackedEqual(source.Read<AotPosition>(root), rootExpected, "Generated root job result");
        RequirePackedEqual(source.Read<AotPosition>(child), childExpected, "Generated child job result");
        Require(source.IsAlive(edge.Entity), "Relation entity was not alive before serialization.");

        using var durable = new MemoryStream();
        WorldSerializer.WriteDurableWorld(durable, source, registry);
        byte[] durableBytes = durable.ToArray();
        durable.Position = 0;
        World loaded = WorldSerializer.ReadDurableWorld(durable, registry);

        RequirePackedEqual(
            loaded.Read<AotPosition>(root),
            rootExpected,
            "Durable root packed component round-trip");
        RequirePackedEqual(
            loaded.Read<AotPosition>(child),
            childExpected,
            "Durable child packed component round-trip");
        Require(
            Hierarchy<AotHierarchyDomain>.GetParent(loaded, child) == root,
            "Hierarchy topology did not round-trip.");
        Require(
            loaded.GetOutgoingRelations<AotLink>(root).Count == 1,
            "Relation adjacency did not round-trip.");
        Require(loaded.Read<AotLink>(edge.Entity).Weight == 7, "Relation payload did not round-trip.");

        using var checkpoint = new MemoryStream();
        WorldSerializer.WriteCheckpointWorld(checkpoint, source, registry);
        checkpoint.Position = 0;
        World checkpointLoaded = WorldSerializer.ReadCheckpointWorld(checkpoint, registry);
        RequirePackedEqual(
            checkpointLoaded.Read<AotPosition>(root),
            rootExpected,
            "Checkpoint root packed component round-trip");
        RequirePackedEqual(
            checkpointLoaded.Read<AotPosition>(child),
            childExpected,
            "Checkpoint child packed component round-trip");

        using var indexedCheckpoint = new MemoryStream();
        WorldCheckpointCodec.Write(indexedCheckpoint, source, registry);
        int indexedCheckpointBytes = checked((int)indexedCheckpoint.Length);
        indexedCheckpoint.Position = 0;
        WorldCheckpointInfo indexedInfo = WorldCheckpointCodec.Inspect(indexedCheckpoint);
        Require(
            indexedInfo.TotalLength == (ulong)indexedCheckpointBytes &&
            indexedInfo.PayloadOffset == WorldCheckpointCodec.HeaderSize &&
            indexedInfo.PayloadOffset + indexedInfo.PayloadLength == indexedInfo.TotalLength,
            "Canonical checkpoint envelope metadata did not match its payload.");
        indexedCheckpoint.Position = 0;
        World indexedCheckpointLoaded = WorldCheckpointCodec.Read(indexedCheckpoint, registry);
        RequirePackedEqual(
            indexedCheckpointLoaded.Read<AotPosition>(root),
            rootExpected,
            "Canonical checkpoint root round-trip");
        RequirePackedEqual(
            indexedCheckpointLoaded.Read<AotPosition>(child),
            childExpected,
            "Canonical checkpoint child round-trip");
        Require(
            Hierarchy<AotHierarchyDomain>.GetParent(indexedCheckpointLoaded, child) == root,
            "Canonical checkpoint hierarchy topology did not round-trip.");
        Require(
            indexedCheckpointLoaded.GetOutgoingRelations<AotLink>(root).Count == 1,
            "Section-indexed relation topology did not round-trip.");

        ulong durableGeneration = ExerciseDurableStore(source, registry, root, rootExpected);
        ExerciseRelationCleanupDispatch();

        WorldStructuralMetrics metrics = source.GetStructuralMetrics();
        Require(metrics.Published > 0, "Structural publication metrics were not populated.");

        long semanticChecksum =
            loaded.Read<AotPosition>(root).Value * 31L +
            loaded.Read<AotPosition>(child).Value * 17L +
            loaded.Read<AotLink>(edge.Entity).Weight;

        return new AotSmokeResult(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            source.EntityCount,
            durableBytes.Length,
            semanticChecksum,
            metrics.Published,
            JobSystem.GetStats().CompletedHandles,
            positionEntry.CodecKind.ToString(),
            asyncJobRejected,
            durableGeneration);
    }

    private static ulong ExerciseDurableStore(
        World source,
        SerializationRegistry registry,
        Entity root,
        AotPosition rootExpected)
    {
        string basePath = Path.Combine(
            Path.GetTempPath(),
            $"SomeEngine.ECS.AotSmoke.{Guid.NewGuid():N}.save");
        var store = new DurableSaveStore(basePath);
        try
        {
            DurableSaveCommit first = store.WriteWorld(source, registry);
            DurableSaveCommit second = store.WriteWorld(source, registry);
            World restored = store.ReadWorld(registry);
            Require(first.Generation == 1, "First durable file generation was not one.");
            Require(second.Generation == 2, "Second durable file generation was not two.");
            RequirePackedEqual(
                restored.Read<AotPosition>(root),
                rootExpected,
                "Durable two-generation file store packed component round-trip");
            return second.Generation;
        }
        finally
        {
            File.Delete(store.PrimaryPath);
            File.Delete(store.PreviousPath);
            File.Delete(basePath + ".lock");
        }
    }

    private static void ExerciseRelationCleanupDispatch()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();

        RelationEdge<AotLink> directlyDestroyed = world.CreateRelation(
            first,
            second,
            new AotLink { Weight = 1 });
        world.DestroyEntity(directlyDestroyed.Entity);
        Require(
            !world.IsAlive(directlyDestroyed.Entity) &&
            world.GetOutgoingRelations<AotLink>(first).Count == 0,
            "Direct edge destruction did not clean relation state.");

        RelationEdge<AotLink> incident = world.CreateRelation(
            first,
            second,
            new AotLink { Weight = 2 });
        world.DestroyEntity(first);
        Require(
            !world.IsAlive(incident.Entity) && world.IsAlive(second),
            "Endpoint destruction did not destroy its incident relation edge.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequirePackedEqual(
        AotPosition actual,
        AotPosition expected,
        string context)
    {
        Require(actual.Marker == expected.Marker, $"{context}: byte field changed.");
        Require(actual.Value == expected.Value, $"{context}: int field changed.");
        Require(
            BitConverter.SingleToInt32Bits(actual.Scale) ==
            BitConverter.SingleToInt32Bits(expected.Scale),
            $"{context}: float bit pattern changed.");
        Require(actual.Stamp == expected.Stamp, $"{context}: long field changed.");
    }
}

internal sealed record AotSmokeResult(
    string Framework,
    string OperatingSystem,
    string Architecture,
    int EntityCount,
    int DurableSaveBytes,
    long SemanticChecksum,
    long StructuralPublications,
    long CompletedJobs,
    string PackedCodec,
    bool AsyncJobRejected,
    ulong DurableGeneration);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AotSmokeResult))]
internal partial class AotSmokeJsonContext : JsonSerializerContext;

internal readonly struct AotHierarchyDomain : IHierarchyDomain;

[SerializableComponent("8E4DA813-885E-42E4-9A21-1DB55F3F35D0")]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal partial struct AotPosition : TableComponent
{
    public byte Marker;
    public int Value;
    public float Scale;
    public long Stamp;
}

[SerializableComponent("DE53213E-C419-45E7-822A-8EA3611524D9")]
internal partial struct AotVelocity : TableComponent
{
    public int Value;
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
[SerializableComponent("F250F947-D403-4A52-B1BB-0DE92E4DBE2E")]
internal partial struct AotLink : TableComponent
{
    public int Weight;
}

internal partial struct AotIntegrateJob : IJobEntity
{
    public void Execute(in AotVelocity velocity, ref AotPosition position)
    {
        position.Value += velocity.Value;
    }
}

internal struct AotAsyncVoidJob : IJob
{
    internal static int BodyCount;

    public async void Execute()
    {
        Interlocked.Increment(ref BodyCount);
        await Task.Yield();
    }
}
