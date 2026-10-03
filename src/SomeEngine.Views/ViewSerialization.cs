using SomeEngine.ECS.Serialization;

namespace SomeEngine.Views;

/// <summary>Registers the persistent view data alongside application components.</summary>
public static class ViewSerialization
{
    /// <summary>Registers node identity, ordered hierarchy, and the view type reader.</summary>
    /// <remarks>Call each view's generated RegisterSerialization method for its parameters.</remarks>
    public static void Register(SerializationRegistry registry)
    {
        registry.Register<ViewType, ViewTypeCodec>(new SerializationTypeKey(
            new Guid("981be364-8cc2-4d7f-9876-59489487713d"), "SomeEngine.Views.ViewType", 1));
        registry.Register<ElementKey, ElementKeyCodec>(new SerializationTypeKey(
            new Guid("41f3acd1-e017-4cf8-9065-c717434f0b69"), "SomeEngine.Views.ElementKey", 1));
        registry.RegisterTag<LocalState>(new SerializationTypeKey(
            new Guid("68e3a075-3919-4d7a-b45e-6c1b466b69c6"), "SomeEngine.Views.LocalState", 1));
        registry.RegisterHierarchyDomain<ElementTree>(new SerializationTypeKey(
            new Guid("b29013d5-d418-4890-9477-c450942c35cb"), "SomeEngine.Views.ElementTree", 1));
    }

    /// <summary>Associates a generated serialization entry with the view's build functions.</summary>
    public static void RegisterType<T>(SerializationTypeKey typeKey)
        where T : struct, IView, IEquatable<T> =>
        ViewTypes.RegisterSerializationId(ViewRegistration<T>.Id, typeKey.StableId);

    private struct ViewTypeCodec : ICanonicalComponentCodec<ViewType>
    {
        public void Write(ref DataWriter writer, in ViewType value) =>
            writer.WriteGuid(ViewTypes.GetSerializationId(value.Id));

        public void Read(ref DataReader reader, out ViewType value) =>
            value = new ViewType { Id = ViewTypes.GetTypeId(reader.ReadGuid()), RebuildRequired = true };
    }

    private struct ElementKeyCodec : ICanonicalComponentCodec<ElementKey>
    {
        public void Write(ref DataWriter writer, in ElementKey value)
        {
            writer.WriteUInt64(value.Key);
            writer.WriteInt32(value.Occurrence);
        }

        public void Read(ref DataReader reader, out ElementKey value) =>
            value = new ElementKey { Key = reader.ReadUInt64(), Occurrence = reader.ReadInt32() };
    }
}
