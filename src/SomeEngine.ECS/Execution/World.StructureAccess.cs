using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private bool IsAliveWithExecution(Entity entity)
    {
        using WorldAccess access = OpenStructure(false);
        return IsAliveCore(entity);
    }
    private bool HasWithExecution<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure(false);
        return HasCore<T>(entity);
    }
    private Entity CreateWithExecution()
    {
        using WorldAccess access = OpenStructure();
        return CreateCore();
    }
    private Entity CreateWithExecution<T>(in T value) where T : struct
    {
        using WorldAccess access = OpenStructure();
        return CreateCore(value);
    }
    private void AddWithExecution<T>(Entity entity, in T value) where T : struct
    {
        using WorldAccess access = OpenStructure();
        AddCore(entity, value);
    }
    private void RemoveWithExecution<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure();
        RemoveCore<T>(entity);
    }
    private void DestroyWithExecution(Entity entity)
    {
        using WorldAccess access = OpenStructure();
        DestroyCore(entity);
    }
}
