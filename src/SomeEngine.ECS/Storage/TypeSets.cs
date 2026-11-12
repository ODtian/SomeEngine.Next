namespace SomeEngine.ECS.Storage;

internal static class TypeSet<T1, T2, T3, T4, T5, T6>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id,
        ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id, ComponentValues.Bind<T6>().Id);
}

internal static class TypeSet<T1, T2>
    where T1 : struct where T2 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id);
}

internal static class TypeSet<T1, T2, T3, T4>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id);
}

internal static class TypeSet<T1, T2, T3, T4, T5>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id);
}
