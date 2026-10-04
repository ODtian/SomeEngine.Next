using SomeEngine.ECS.Components;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>A scrollable viewport whose content is declared by its caller.</summary>
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
