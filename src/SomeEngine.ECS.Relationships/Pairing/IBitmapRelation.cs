namespace SomeEngine.ECS.Relations;

/// <summary>Associates a semantic relation with concrete category-bit reads and a permission operation.</summary>
public interface IBitmapRelation<TRelation, TComponent, TPermission>
    : IRelationImplementation<TRelation>, ICategoryBitsReader<TRelation, TComponent>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{ }

/// <summary>Reads each mask from a user component in the declared category domain.</summary>
public interface ICategoryBitsReader<TRelation, TComponent>
    where TRelation : struct
    where TComponent : struct
{
    ulong ReadCategories(in TRelation relation, in TComponent component);

    ulong ReadAllowedCategories(in TRelation relation, in TComponent component);
}
