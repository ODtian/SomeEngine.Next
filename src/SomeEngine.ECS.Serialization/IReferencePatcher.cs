using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

public interface IReferenceRemapper
{
    bool TryMap(Entity source, out Entity mapped);
}

public interface IReferencePatcher<T>
    where T : struct
{
    void Remap(ref T value, IReferenceRemapper remapper);
}

