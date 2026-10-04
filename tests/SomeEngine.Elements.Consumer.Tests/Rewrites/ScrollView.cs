using SomeEngine.ECS.Components;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A caller-content scroll viewport written with public behavior declarations.</summary>
public record struct ScrollView() : IView, ISparseComponent
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Group, "");
        ScrollableFunctions.Scrollable(Scope.Entity);
        return default;
    }
}
