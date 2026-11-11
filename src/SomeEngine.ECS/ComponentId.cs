using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public static class ComponentId<T> where T : struct
{
    private static readonly int Type = ComponentValues.Bind<T>().Id;
    public static int Value => Type;
}
