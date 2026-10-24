namespace SomeEngine.ECS.Relations;

internal interface IRelationComponentPresence
{
    static abstract bool IsRequired { get; }
}

internal readonly struct RequiredRelationComponents : IRelationComponentPresence
{
    public static bool IsRequired => true;
}

internal readonly struct OptionalRelationComponents : IRelationComponentPresence
{
    public static bool IsRequired => false;
}
