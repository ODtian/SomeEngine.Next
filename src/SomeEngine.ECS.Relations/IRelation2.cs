namespace SomeEngine.ECS.Relations;

public interface IRelation<T1, T2>
{
    bool Matches(in T1 first, in T2 second);
}
