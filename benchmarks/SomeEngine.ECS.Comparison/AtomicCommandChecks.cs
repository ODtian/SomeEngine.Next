using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class AtomicCommandChecks
{
    internal static void Run()
    {
        CapturedConditionsBindTheCandidate();
        using var source = new SourceFullArchiveWorld(128, true);
        int count = source.World.EntityCount;
        ulong version = source.World.Version;
        using var result = new World(chunkCapacity: source.World.ChunkCapacity);
        var systems = new SaveSystemVersions();
        DeferredEntity created = default;
        PlaybackAtomic(source.World, result, [source.Systems], [systems], commands =>
        {
            commands.Set(source.Entities[1], new Position { X = 701 });
            commands.SetBuffer<SaveLink>(source.Entities[1], [new(source.Entities[2], 703)]);
            created = commands.Create(new Position { X = 709 });
        });
        Require(result.Read<Position>(source.Entities[1]).X == 701
            && result.Read<Position>(created.Resolve()).X == 709
            && result.EntityCount == count + 1, "successful atomic composition returns the complete candidate world");
        using (var buffer = result.ReadBuffer<SaveLink>(source.Entities[1]))
            Require(buffer.Count == 1 && buffer[0] == new SaveLink(source.Entities[2], 703),
                "candidate commands write independently owned buffer contents");
        Require(Hierarchy<SaveTree>.GetParent(result, source.Entities[0]) == source.SecondRoot
            && systems.Values.AsSpan().SequenceEqual(source.Systems.Values),
            "preserve capture restores deferred topology and matching system progress before recording");
        source.Check(true);
        Require(source.World.EntityCount == count && source.World.Version == version,
            "successful candidate playback preserves the source world data and version");

        var failed = new World();
        bool faulted = false;
        try
        {
            PlaybackAtomic(source.World, failed, [source.Systems], [new SaveSystemVersions()], commands =>
            {
                commands.Set(source.Entities[1], new Position { X = 719 });
                commands.Set(source.Dead, new Position { X = 727 });
            });
        }
        catch (ArgumentException) { faulted = true; }
        Require(faulted, "a command failure ends atomic candidate playback");
        source.Check(true);
        Require(source.World.EntityCount == count && source.World.Version == version,
            "failed candidate disposal preserves the complete source world");
        Console.WriteLine("Atomic command composition: complete preserve capture, candidate recording, independent buffers, system progress, successful handoff and failed-candidate disposal passed.");
    }

    private static void CapturedConditionsBindTheCandidate()
    {
        using var source = new SourceFullArchiveWorld(16, true);
        using var candidate = new World();
        PlaybackAtomic(source.World, candidate, [source.Systems], [new SaveSystemVersions()], commands =>
            commands.EditParents<SaveTree>([new CapturedParentEdit(source.Entities[0], source.SecondRoot, source.FirstRoot)],
                candidate.DataGeneration));
        Require(Hierarchy<SaveTree>.GetParent(candidate, source.Entities[0]) == source.FirstRoot
            && Hierarchy<SaveTree>.GetParent(source.World, source.Entities[0]) == source.SecondRoot,
            "captured parent conditions use the candidate generation and preserve the source");
    }

    private static void PlaybackAtomic(World source, World candidate,
        ISystemVersions[] sourceSystems, ISystemVersions[] candidateSystems, Action<CommandBuffer> record)
    {
        try
        {
            using var data = new WorldCheckpoint();
            data.Capture(source, CheckpointProgress.Preserve, sourceSystems);
            data.Restore(candidate, candidateSystems);
            using var commands = new CommandBuffer(candidate);
            record(commands);
            commands.Playback();
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
}
