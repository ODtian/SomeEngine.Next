namespace SomeEngine.ECS.Relations;

/// <summary>Associates a semantic relation with concrete category-bit reads and a permission operation.</summary>
public interface IBitmapRelation<TRelation, TComponent, TPermission> : IRelationImplementation<TRelation>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    CategoryBits Read(in TRelation relation, in TComponent component);
}
