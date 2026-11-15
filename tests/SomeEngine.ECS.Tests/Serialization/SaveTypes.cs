namespace SomeEngine.ECS.Serialization.Tests;

internal static class SaveTypes
{
    internal static SerializationTypeKey Key<T>() => new(typeof(T).GUID, typeof(T).FullName!, 1);
}

internal enum SavedPayloadKind : byte { World, Component }
