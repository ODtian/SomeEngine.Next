namespace SomeEngine.ECS.Relations;

internal interface IRelationSelection
{
    static abstract bool RequiresMatch { get; }
}

internal readonly struct WhereSelection : IRelationSelection
{
    public static bool RequiresMatch => true;
}

internal readonly struct ExceptSelection : IRelationSelection
{
    public static bool RequiresMatch => false;
}
