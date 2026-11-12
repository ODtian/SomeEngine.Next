namespace SomeEngine.ECS.Serialization;

public sealed partial class SerializationRegistry
{
    private readonly HashSet<Guid> _destroyedReaders = [];

    internal void BindDestroyedReaders(World world)
    {
        foreach (Guid consumer in _destroyedReaders) world.WatchDestroyed(consumer);
    }

    public SerializationRegistry RegisterDestroyedReader(Guid consumerId)
    {
        if (consumerId == Guid.Empty) throw new ArgumentException("A named consumer requires a nonempty identity.", nameof(consumerId));
        _destroyedReaders.Add(consumerId);
        return this;
    }
}
