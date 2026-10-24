using System.ComponentModel;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Registry;

/// <summary>Receives the type data emitted by the ECS source generator.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedEcsTypes
{
    public static void RegisterJobStorage<T>(bool isAliasFree) where T : struct =>
        JobStorageTypeMetadata<T>.Register(isAliasFree);

    public static void RegisterRelationSchema<T>(RelationSchema schema) where T : struct, IComponent =>
        RelationSchemaCache<T>.Register(schema);
}
