using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public static class GeneratedEcsTypes
{
    public static void RegisterJobStorage<T>(bool isAliasFree) where T : struct
        => JobStorageTypeMetadata<T>.Register(isAliasFree);
}
