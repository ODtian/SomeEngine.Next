using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal interface ITopologySerializationTypes
{
    void Hierarchy<TDomain>(ITopologySerializationType type) where TDomain : struct, IHierarchyDomain;
    void Relation<T>(ITopologySerializationType type) where T : struct;
}

internal interface ITopologySerializationType
{
    object? Description { get; set; }
    void Describe(ITopologySerializationTypes types);
}

internal sealed class HierarchySerializationType<TDomain> : ITopologySerializationType where TDomain : struct, IHierarchyDomain
{
    public object? Description { get; set; }
    public void Describe(ITopologySerializationTypes types) => types.Hierarchy<TDomain>(this);
    internal static ITopologySerializationType Register()
    {
        ComponentType type = ComponentValues.Bind<Parent<TDomain>>();
        ComponentValues.Bind<Children<TDomain>>();
        lock (type) return (ITopologySerializationType)(type.SerializationDescription ??= new HierarchySerializationType<TDomain>());
    }
}

internal sealed class RelationSerializationType<T> : ITopologySerializationType where T : struct
{
    public object? Description { get; set; }
    public void Describe(ITopologySerializationTypes types) => types.Relation<T>(this);
    internal static ITopologySerializationType Register()
    {
        ComponentType type = ComponentValues.Bind<RelationEndpoints<T>>();
        lock (type) return (ITopologySerializationType)(type.SerializationDescription ??= new RelationSerializationType<T>());
    }
}
