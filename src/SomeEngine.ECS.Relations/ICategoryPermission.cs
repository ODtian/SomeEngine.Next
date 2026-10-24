namespace SomeEngine.ECS.Relations;

/// <summary>Tests category-mask intersection in one direction or both directions.</summary>
public interface ICategoryPermission
{
    static abstract bool RequiresBothDirections { get; }

    static abstract bool Matches(in CategoryBits first, in CategoryBits second);
}

public readonly struct OneWayPermission : ICategoryPermission
{
    public static bool RequiresBothDirections => false;

    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        (first.AllowedCategories & second.Categories) != 0;
}

public readonly struct MutualPermission : ICategoryPermission
{
    public static bool RequiresBothDirections => true;

    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        (first.AllowedCategories & second.Categories) != 0 &&
        (second.AllowedCategories & first.Categories) != 0;
}
