using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.ECS.Serialization;

public sealed partial class ComponentCodec
{
    private static readonly TopologyTypes Topologies = new();

    public static void RegisterDefaultHierarchy<TDomain>() where TDomain : struct, IHierarchyDomain
        => Describe(HierarchySerializationType<TDomain>.Register());

    public static void RegisterDefaultRelation<T>() where T : struct
        => Describe(RelationSerializationType<T>.Register());

    internal static ComponentCodec Describe(ITopologySerializationType type)
    {
        lock (type)
        {
            if (type.Description is null) type.Describe(Topologies);
            return (ComponentCodec)type.Description!;
        }
    }

    private static SerializationTypeKey TopologyFormat<T>() where T : struct
    {
        string name = TopologyName(typeof(T));
        Span<byte> identity = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), identity);
        return new(new Guid(identity[..16]), name, 1);
    }

    private static string TopologyName(Type type)
        => type.IsGenericType
            ? type.Assembly.GetName().Name + ":" + type.GetGenericTypeDefinition().FullName + "[" + string.Join(',', type.GetGenericArguments().Select(TopologyName)) + "]"
            : type.Assembly.GetName().Name + ":" + type.FullName;

    private sealed class TopologyTypes : ITopologySerializationTypes
    {
        public void Hierarchy<TDomain>(ITopologySerializationType type) where TDomain : struct, IHierarchyDomain
        {
            var registry = new SerializationRegistry().RegisterHierarchy<TDomain>(TopologyFormat<Parent<TDomain>>());
            type.Description = FromTopology(ComponentType<Parent<TDomain>>.Value, registry);
        }

        public void Relation<T>(ITopologySerializationType type) where T : struct
        {
            var registry = new SerializationRegistry().RegisterRelationTopology<T>(TopologyFormat<RelationEndpoints<T>>());
            type.Description = FromTopology(ComponentType<RelationEndpoints<T>>.Value, registry);
        }
    }

    private static ComponentCodec FromTopology(ComponentType component, SerializationRegistry registry)
    {
        SavedType value = registry.Columns[component.Id];
        return new(component, value,
            registry.Types.Where(type => type != value && type.Type != ComponentType<Destroyed>.TypeId).ToArray(),
            registry.Services.ToArray());
    }
}
