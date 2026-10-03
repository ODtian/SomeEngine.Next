using SomeEngine.ECS;

namespace SomeEngine.Views;

/// <summary>A transparent container whose caller declares its children.</summary>
public record struct Group : IView, IComponent
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build() => default;
}
