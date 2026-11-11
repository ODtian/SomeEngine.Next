using System.Buffers;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class NamedDestroyedReaderChecks
{
    private static readonly Guid First = new("8a6eac96-ea7c-4dda-bcd9-c2114b63f001");
    private static readonly Guid Second = new("8a6eac96-ea7c-4dda-bcd9-c2114b63f002");
    private static readonly Guid Added = new("8a6eac96-ea7c-4dda-bcd9-c2114b63f003");

    internal static void Run()
    {
        MemoryProgressFollowsConsumerIdentity();
        MixedReadersKeepTheirOwnPositions();
        NewSerializedConsumersStartAtThrough();
        RemovingNamedReaderKeepsAnonymousOrder();
        RegisteredConsumerObservesAfterPlainLoad();
        Console.WriteLine("Named destroyed consumers: cross-World identity, mixed positions and new consumer baselines passed.");
    }

    private static void MemoryProgressFollowsConsumerIdentity()
    {
        using var source = new World();
        var first = source.WatchDestroyed(First);
        var second = source.WatchDestroyed(Second);
        source.Destroy(source.Create());
        first.Advance(1);
        using var worldCheckpoint = new WorldCheckpoint();
        worldCheckpoint.Capture(source, CheckpointProgress.Preserve);
        using var target = new World();
        var targetSecond = target.WatchDestroyed(Second);
        var targetAdded = target.WatchDestroyed(Added);
        var targetFirst = target.WatchDestroyed(First);
        worldCheckpoint.Restore(target);
        Require(ReferenceEquals(target.WatchDestroyed(First), targetFirst)
            && targetFirst.Position == 1 && targetSecond.Position == 0 && targetAdded.Position == 1,
            "checkpoint consumers match identities across registration order and added consumers");
        using var unread = targetSecond.Open();
        Require(unread.Count == 1, "the restored consumer retains its unread destroyed identity");
    }

    private static void MixedReadersKeepTheirOwnPositions()
    {
        using var source = new World();
        var anonymousFirst = source.WatchDestroyed();
        var named = source.WatchDestroyed(First);
        var anonymousSecond = source.WatchDestroyed();
        source.Destroy(source.Create());
        anonymousFirst.Advance(1);
        named.Advance(1);
        var registry = new SerializationRegistry().RegisterDestroyedReader(First);
        var serializer = new WorldSerializer(registry);
        var bytes = new ArrayBufferWriter<byte>();
        serializer.Save(source, bytes, progress: CheckpointProgress.Preserve);
        using var target = new World();
        var targetFirst = target.WatchDestroyed();
        var targetSecond = target.WatchDestroyed();
        var targetNamed = target.WatchDestroyed(First);
        serializer.Load(target, bytes.WrittenSpan);
        Require(targetFirst.Position == 1 && targetSecond.Position == 0 && targetNamed.Position == 1,
            "anonymous positions exclude named consumers in a mixed serialized registration");
    }

    private static void NewSerializedConsumersStartAtThrough()
    {
        using var source = new World();
        source.WatchDestroyed(First);
        source.Destroy(source.Create());
        var bytes = new ArrayBufferWriter<byte>();
        new WorldSerializer(new SerializationRegistry().RegisterDestroyedReader(First))
            .Save(source, bytes, progress: CheckpointProgress.Preserve);
        using var target = new World();
        new WorldSerializer(new SerializationRegistry().RegisterDestroyedReader(Added).RegisterDestroyedReader(First))
            .Load(target, bytes.WrittenSpan);
        Require(target.WatchDestroyed(First).Position == 0 && target.WatchDestroyed(Added).Position == 1,
            "new named consumers start at the loaded through sequence");
    }

    private static void Require(bool condition, string behaviour)
    {
        if (!condition) throw new InvalidOperationException(behaviour);
    }

    private static void RemovingNamedReaderKeepsAnonymousOrder()
    {
        using var source = new World();
        var a = source.WatchDestroyed();
        var named = source.WatchDestroyed(First);
        var b = source.WatchDestroyed();
        var c = source.WatchDestroyed();
        source.Destroy(source.Create());
        a.Advance(1); named.Advance(1); c.Advance(1);
        named.Dispose();
        using var worldCheckpoint = new WorldCheckpoint();
        worldCheckpoint.Capture(source, CheckpointProgress.Preserve);
        using var target = new World();
        var targetA = target.WatchDestroyed();
        var targetB = target.WatchDestroyed();
        var targetC = target.WatchDestroyed();
        worldCheckpoint.Restore(target);
        Require(targetA.Position == 1 && targetB.Position == 0 && targetC.Position == 1,
            "removing a named consumer preserves anonymous registration order");
    }

    private static void RegisteredConsumerObservesAfterPlainLoad()
    {
        using var source = new World();
        var bytes = new ArrayBufferWriter<byte>();
        new WorldSerializer(new SerializationRegistry()).Save(source, bytes, progress: CheckpointProgress.Preserve);
        using var target = new World();
        new WorldSerializer(new SerializationRegistry().RegisterDestroyedReader(Added)).Load(target, bytes.WrittenSpan);
        target.Destroy(target.Create());
        using var unread = target.WatchDestroyed(Added).Open();
        Require(unread.Count == 1, "a target registered consumer observes destruction immediately after loading a plain source");
    }
}
