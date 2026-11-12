using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Commands;

internal sealed class CommandResults
{
    internal Entity[] Created = [];
    internal int[]? Offsets;
    internal int CompletedCount;
    internal int CompletedCompositeCreates;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Index(int producer, int local) => (Offsets is { } offsets ? offsets[producer] : 0) + local;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity Decode(Entity target) => target.Id >= 0 ? target
        : Created[Index(target.Generation - 1, -target.Id - 1)];
}

public readonly struct DeferredEntity
{
    internal readonly CommandResults? Results;
    internal readonly int Producer, LocalId;

    internal DeferredEntity(CommandResults results, int producer, int localId)
        => (Results, Producer, LocalId) = (results, producer, localId);

    public bool TryResolve(out Entity entity)
    {
        if (Results is { } results)
        {
            int completed = Volatile.Read(ref results.CompletedCount);
            int index = results.Index(Producer, LocalId);
            if (index < completed) { entity = results.Created[index]; return true; }
        }
        entity = default;
        return false;
    }

    public Entity Resolve() => TryResolve(out Entity entity) ? entity
        : throw new InvalidOperationException("Read a create result after playback completes its command.");
}

public readonly struct CommandEntity
{
    internal readonly CommandResults? Results;
    internal readonly int Id, Generation;

    public CommandEntity(Entity entity) => (Results, Id, Generation) = (null, entity.Id, entity.Generation);
    public CommandEntity(DeferredEntity entity) => (Results, Id, Generation) = (entity.Results, entity.LocalId, entity.Producer);
    public static CommandEntity CreatedInBatch(int index) => new(new Entity(-index - 1, 0));
    public static implicit operator CommandEntity(Entity entity) => new(entity);
    public static implicit operator CommandEntity(DeferredEntity entity) => new(entity);
}
