using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Strongly named entry points for callers that want an explicit persistence contract instead of
/// the general Write* entry points' RawCheckpoint default.
/// </summary>
public static partial class WorldSerializer
{
    public static void WriteDurableComponent<T>(
        Stream stream,
        in T value,
        SerializationRegistry registry)
        where T : struct =>
        WriteComponent(
            stream,
            in value,
            registry,
            new SerializeOptions(Contract: SerializationContract.DurableSave));

    public static void WriteCheckpointComponent<T>(
        Stream stream,
        in T value,
        SerializationRegistry registry)
        where T : struct =>
        WriteComponent(
            stream,
            in value,
            registry,
            new SerializeOptions(Contract: SerializationContract.RawCheckpoint));

    public static void WriteDurableEntity(
        Stream stream,
        World world,
        Entity entity,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteEntity(stream, world, entity, registry, options with
        {
            Contract = SerializationContract.DurableSave,
        });

    public static void WriteCheckpointEntity(
        Stream stream,
        World world,
        Entity entity,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteEntity(stream, world, entity, registry, options with
        {
            Contract = SerializationContract.RawCheckpoint,
        });

    public static void WriteDurableEntities(
        Stream stream,
        World world,
        ReadOnlySpan<Entity> entities,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteEntities(stream, world, entities, registry, options with
        {
            Contract = SerializationContract.DurableSave,
        });

    public static void WriteCheckpointEntities(
        Stream stream,
        World world,
        ReadOnlySpan<Entity> entities,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteEntities(stream, world, entities, registry, options with
        {
            Contract = SerializationContract.RawCheckpoint,
        });

    public static void WriteDurableQuery(
        Stream stream,
        World world,
        QueryHandle query,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteQuery(stream, world, query, registry, options with
        {
            Contract = SerializationContract.DurableSave,
        });

    public static void WriteCheckpointQuery(
        Stream stream,
        World world,
        QueryHandle query,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteQuery(stream, world, query, registry, options with
        {
            Contract = SerializationContract.RawCheckpoint,
        });

    public static void WriteDurableWorld(
        Stream stream,
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteWorld(stream, world, registry, options with
        {
            Contract = SerializationContract.DurableSave,
        });

    public static void WriteCheckpointWorld(
        Stream stream,
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteWorld(stream, world, registry, options with
        {
            Contract = SerializationContract.RawCheckpoint,
        });

    public static T ReadDurableComponent<T>(
        Stream stream,
        SerializationRegistry registry,
        SerializationReadLimits? limits = null)
        where T : struct =>
        ReadComponent<T>(
            stream,
            registry,
            new SerializationReadOptions(limits, SerializationContract.DurableSave));

    public static T ReadCheckpointComponent<T>(
        Stream stream,
        SerializationRegistry registry,
        SerializationReadLimits? limits = null)
        where T : struct =>
        ReadComponent<T>(
            stream,
            registry,
            new SerializationReadOptions(limits, SerializationContract.RawCheckpoint));

    public static World ReadDurableWorld(
        Stream stream,
        SerializationRegistry registry,
        WorldLoadOptions options = default) =>
        ReadWorld(stream, registry, options with
        {
            RequiredContract = SerializationContract.DurableSave,
        });

    public static World ReadCheckpointWorld(
        Stream stream,
        SerializationRegistry registry,
        WorldLoadOptions options = default) =>
        ReadWorld(stream, registry, options with
        {
            RequiredContract = SerializationContract.RawCheckpoint,
        });

}
