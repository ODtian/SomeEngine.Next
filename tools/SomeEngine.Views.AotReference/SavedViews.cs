using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Views.Compose;

namespace SomeEngine.Views.AotReference;

public static partial class SavedComposeViews
{
    [Composable]
    public static void Hud(Entity source) => Scope.View(new SavedValue(Scope.Read<SavedCount>(source).Value), 1);
}

public readonly record struct SavedValue(int Value) : IView, ISparseComponent
{
    public BuildTask Build() => default;
}

public readonly record struct SavedCount(int Value) : IComponent;

public struct SavedCountCodec : ICanonicalComponentCodec<SavedCount>
{
    public void Write(ref DataWriter writer, in SavedCount value) => writer.WriteInt32(value.Value);
    public void Read(ref DataReader reader, out SavedCount value) => value = new SavedCount(reader.ReadInt32());
}
