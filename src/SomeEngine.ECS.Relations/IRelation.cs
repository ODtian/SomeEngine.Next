namespace SomeEngine.ECS.Relations;

public interface IRelation<T1>
{
    bool Matches(in T1 first);
}
