namespace SomeEngine.ECS.Storage;

internal static class TypeSet<T1, T2, T3, T4, T5, T6, T7>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id, ComponentValues.Bind<T6>().Id, ComponentValues.Bind<T7>().Id);
}

internal static class TypeSet<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id, ComponentValues.Bind<T6>().Id, ComponentValues.Bind<T7>().Id, ComponentValues.Bind<T8>().Id);
}

internal static class TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id, ComponentValues.Bind<T6>().Id, ComponentValues.Bind<T7>().Id, ComponentValues.Bind<T8>().Id, ComponentValues.Bind<T9>().Id);
}

internal static class TypeSet<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    where T1 : struct where T2 : struct where T3 : struct where T4 : struct where T5 : struct where T6 : struct where T7 : struct where T8 : struct where T9 : struct where T10 : struct
{
    internal static readonly TypeSet Value = new(ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id, ComponentValues.Bind<T4>().Id, ComponentValues.Bind<T5>().Id, ComponentValues.Bind<T6>().Id, ComponentValues.Bind<T7>().Id, ComponentValues.Bind<T8>().Id, ComponentValues.Bind<T9>().Id, ComponentValues.Bind<T10>().Id);
}
