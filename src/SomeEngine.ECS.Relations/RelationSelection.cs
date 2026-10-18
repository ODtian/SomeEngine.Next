namespace SomeEngine.ECS.Relations;

internal interface IRelationSelection
{
    static abstract bool Accepts(bool matches);
}

internal readonly struct WhereSelection : IRelationSelection
{
    public static bool Accepts(bool matches) => matches;
}

internal readonly struct ExceptSelection : IRelationSelection
{
    public static bool Accepts(bool matches) => !matches;
}
