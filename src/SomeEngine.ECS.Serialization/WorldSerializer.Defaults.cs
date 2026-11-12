namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer
{
    public static void WriteWorld(Stream output, World world, SerializeOptions options = default)
        => new WorldSerializer(SerializationRegistry.Bind(null, options.Codecs))
            .Save(world, output, options with { Codecs = default });

    public static World ReadWorld(Stream input, WorldLoadOptions options = default)
        => ReadWorld(input, SerializationRegistry.Bind(null, options.Codecs), options with { Codecs = default });

    public static World ReadWorld<TBinding>(Stream input, WorldLoadOptions options, in TBinding bindSystems)
        where TBinding : struct, IWorldSystemBinding
        => ReadWorld(input, SerializationRegistry.Bind(null, options.Codecs), options with { Codecs = default }, bindSystems);

    public static void WriteComponent<T>(Stream output, in T value, SerializeOptions options = default) where T : struct
        => WriteComponent(output, value, SerializationRegistry.Bind(null, options.Codecs), options with { Codecs = default });

    public static T ReadComponent<T>(Stream input, SerializationReadOptions options = default) where T : struct
        => ReadComponent<T>(input, SerializationRegistry.Bind(null, options.Codecs), options with { Codecs = default });
}
