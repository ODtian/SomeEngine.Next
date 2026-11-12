namespace SomeEngine.ECS.Relations;

/// <summary>Associates a semantic relation with a concrete implementation at compile time.</summary>
public interface IRelationImplementation<TRelation>
    where TRelation : struct;
