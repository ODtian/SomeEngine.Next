namespace SomeEngine.ECS.Relations;

public interface IRelationSelection
{
    static abstract bool RequiresMatch { get; }
}

public readonly struct WhereSelection : IRelationSelection
{
    public static bool RequiresMatch => true;
}

public readonly struct ExceptSelection : IRelationSelection
{
    public static bool RequiresMatch => false;
}
