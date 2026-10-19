using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Tests category-mask intersection in one direction or both directions.</summary>
public interface ICategoryPermission
{
    static abstract bool RequiresBothDirections { get; }

    static abstract bool Matches(in CategoryBits first, in CategoryBits second);

    static abstract bool Matches<TRelation, TComponent, TReader>(in TRelation relation, ref TReader reader,
        in TComponent first, in TComponent second)
        where TRelation : struct
        where TComponent : struct
        where TReader : struct, ICategoryBitsReader<TRelation, TComponent>;
}

public readonly struct OneWayPermission : ICategoryPermission
{
    public static bool RequiresBothDirections => false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        CategoryBits.Allows(first.AllowedCategories, second.Categories);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches<TRelation, TComponent, TReader>(in TRelation relation, ref TReader reader,
        in TComponent first, in TComponent second)
        where TRelation : struct
        where TComponent : struct
        where TReader : struct, ICategoryBitsReader<TRelation, TComponent> =>
        CategoryBits.Allows(reader.ReadAllowedCategories(in relation, in first),
            reader.ReadCategories(in relation, in second));
}

public readonly struct MutualPermission : ICategoryPermission
{
    public static bool RequiresBothDirections => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        OneWayPermission.Matches(in first, in second) && OneWayPermission.Matches(in second, in first);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matches<TRelation, TComponent, TReader>(in TRelation relation, ref TReader reader,
        in TComponent first, in TComponent second)
        where TRelation : struct
        where TComponent : struct
        where TReader : struct, ICategoryBitsReader<TRelation, TComponent> =>
        OneWayPermission.Matches<TRelation, TComponent, TReader>(in relation, ref reader, in first, in second) &&
        OneWayPermission.Matches<TRelation, TComponent, TReader>(in relation, ref reader, in second, in first);
}
