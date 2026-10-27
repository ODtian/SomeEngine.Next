using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class JobStorageSmoke
{
    internal static void CheckBridge()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("The storage bridge probe must run as NativeAOT.");

        ref int cache = ref BridgeCache(null);
        Require(cache == 0 && BridgeStorage<BridgeValue>.Read() == 0,
            "The private closed-generic cache did not start at zero.");
        try
        {
            Volatile.Write(ref cache, 1);
            Require(BridgeStorage<BridgeValue>.Read() == 1,
                "The accessor write did not reach the typed private generic cache.");
            Require(BridgeStorage<OtherBridgeValue>.Read() == 0,
                "The accessor changed another closed generic cache.");
            Volatile.Write(ref cache, -1);
            Require(BridgeStorage<BridgeValue>.Read() == -1,
                "The accessor did not preserve the explicit-false cache value.");
        }
        finally
        {
            Volatile.Write(ref cache, 0);
        }
        Require(BridgeStorage<BridgeValue>.Read() == 0,
            "The accessor did not restore the original cache.");
    }

    internal static void Check()
    {
        CheckBridge();
        using var world = WorldJobs.CreateWorld();
        CheckPrivateStorage(world);
        CheckPrivateGenericStorage(world);
        CheckIndirectRegistration<long>(world);
    }

    private static void CheckPrivateStorage(World world)
    {
        Entity entity = world.CreateEntity(new PrivateTable { Value = new SafeLeaf { Value = 17 } });
        var values = new List<long>();
        ComponentJobAccess<PrivateTable>.ScheduleRead(world, new ReadPrivate(world, entity, values)).Complete();
        Require(values.SequenceEqual([17L]), "Private numeric storage did not run its typed read.");
        Expect<InvalidOperationException>(() => ComponentJobAccess<PrivateAlias>.Read(world),
            "Private nested native-handle storage was not classified as an alias.");
        Expect<InvalidOperationException>(() => ComponentJobAccess<PrivateFixedBuffer>.Read(world),
            "Private fixed-buffer storage was not classified as an alias.");
    }

    private static void CheckPrivateGenericStorage(World world)
    {
        Entity entity = world.CreateEntity(new PrivateGeneric<SafeLeaf> { Value = new SafeLeaf { Value = 23 } });
        var values = new List<long>();
        ComponentJobAccess<PrivateGeneric<SafeLeaf>>.ScheduleRead(
            world, new ReadPrivateGeneric(world, entity, values)).Complete();
        Require(values.SequenceEqual([23L]), "Private closed-generic numeric storage did not run its typed read.");
        Expect<InvalidOperationException>(() => ComponentJobAccess<PrivateGeneric<NativeLeaf>>.Read(world),
            "Private closed-generic native-handle storage was not classified as an alias.");
        var definition = world.QueryDefinition().Read<PrivateGeneric<NativeLeaf>>().Build();
        var accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        Expect<InvalidOperationException>(() => QueryJobAccess.CopyAccesses(world, definition, accesses),
            "Query access did not observe the same private closed-generic alias classification.");

        Entity nested = world.CreateEntity(new PrivateOwner<SafeLeaf>.Nested<SmallSafeLeaf>
            { First = new SafeLeaf { Value = 17 }, Second = new SmallSafeLeaf { Value = 29 } });
        var nestedValues = new List<long>();
        ComponentJobAccess<PrivateOwner<SafeLeaf>.Nested<SmallSafeLeaf>>.ScheduleRead(
            world, new ReadNestedPrivateGeneric(world, nested, nestedValues)).Complete();
        Require(nestedValues.SequenceEqual([1729L]), "A generic containing type lost its outer-to-inner type argument identity.");
        Expect<InvalidOperationException>(() => ComponentJobAccess<PrivateOwner<SafeLeaf>.Nested<NativeLeaf>>.Read(world),
            "A private generic owner's nested native handle was not classified as an alias.");
    }

    // No closed Indirect<T> is spelled outside this generic helper or its generic Job.
    // The source generator sees the open storage type and the call's long argument separately.
    private static void CheckIndirectRegistration<T>(World world) where T : unmanaged
    {
        Entity entity = world.CreateEntity(new Indirect<T> { Marker = 31 });
        var values = new List<long>();
        var job = new ReadIndirect<T>(world, entity, values);
        Expect<NotSupportedException>(() => ComponentJobAccess<Indirect<T>>.ScheduleRead(world, job),
            "Unregistered helper-created storage was admitted by NativeAOT.");
        Expect<NotSupportedException>(() => ComponentJobAccess<Indirect<T>>.Read(world),
            "Missing metadata was cached as an ordinary alias rejection.");
        Require(values.Count == 0 && world.Read<Indirect<T>>(entity).Marker == 31,
            "Rejected missing-metadata access executed its callback or changed the value.");

        GeneratedEcsTypes.RegisterJobStorage<Indirect<T>>(true);
        ComponentJobAccess<Indirect<T>>.ScheduleRead(world, job).Complete();
        Require(values.SequenceEqual([31L]), "Explicit safe registration did not recover typed scheduling.");

        GeneratedEcsTypes.RegisterJobStorage<Indirect<T>>(false);
        Expect<InvalidOperationException>(() => ComponentJobAccess<Indirect<T>>.ScheduleRead(world, job),
            "Explicit false registration was not an ordinary alias rejection.");
        Require(values.SequenceEqual([31L]), "Explicit false registration allowed a callback.");

        GeneratedEcsTypes.RegisterJobStorage<Indirect<T>>(true);
        ComponentJobAccess<Indirect<T>>.ScheduleRead(world, job).Complete();
        Require(values.SequenceEqual([31L, 31L]), "Re-registering safe storage did not restore the same cache.");
    }

    private readonly struct ReadPrivate(World world, Entity entity, List<long> values) : IJob
    {
        public void Execute() => values.Add(world.Read<PrivateTable>(entity).Value.Value);
    }

    private readonly struct ReadPrivateGeneric(World world, Entity entity, List<long> values) : IJob
    {
        public void Execute() => values.Add(world.Read<PrivateGeneric<SafeLeaf>>(entity).Value.Value);
    }

    private readonly struct ReadNestedPrivateGeneric(World world, Entity entity, List<long> values) : IJob
    {
        public void Execute()
        {
            var value = world.Read<PrivateOwner<SafeLeaf>.Nested<SmallSafeLeaf>>(entity);
            values.Add(value.First.Value * 100 + value.Second.Value);
        }
    }

    private readonly struct ReadIndirect<T>(World world, Entity entity, List<long> values) : IJob where T : unmanaged
    {
        public void Execute() => values.Add(world.Read<Indirect<T>>(entity).Marker);
    }

    private struct SafeLeaf { internal long Value { get; set; } }
    private struct SmallSafeLeaf { internal int Value { get; set; } }
    private static class PrivateOwner<T> where T : unmanaged
    {
        internal struct Nested<U> : IComponent where U : unmanaged
        {
            internal T First { get; set; }
            internal U Second { get; set; }
        }
    }
    private struct NativeLeaf { internal nuint Value { get; set; } }
    private struct PrivateTable : IComponent { internal SafeLeaf Value { get; set; } }
    private struct PrivateAlias : IComponent { internal NativeLeaf Value { get; set; } }
    private struct PrivateGeneric<T> : IComponent where T : unmanaged { internal T Value { get; set; } }
    private struct Indirect<T> : IComponent where T : unmanaged { internal long Marker { get; set; } }
    private unsafe struct PrivateFixedBuffer : IComponent { internal fixed byte Values[4]; }

    private static void Expect<TException>(Action action, string behavior) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(behavior);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_value")]
    private static extern ref int BridgeCache(
        [UnsafeAccessorType("SomeEngine.Tools.EcsAotSmoke.JobStorageSmoke+BridgeStorage`1[[SomeEngine.Tools.EcsAotSmoke.JobStorageSmoke+BridgeValue, SomeEngine.ECS.AotSmoke]], SomeEngine.ECS.AotSmoke")]
        object? declaringType);

    private static class BridgeStorage<T> where T : struct
    {
        private static int s_value = 0;
        internal static int Read() => Volatile.Read(ref s_value);
    }

    private readonly struct BridgeValue;
    private readonly struct OtherBridgeValue;

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
}
