using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements;

/// <summary>A caller-controlled switch composed from public input and surface data.</summary>
public record struct Toggle(bool On) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Toggle, "");
        PressableFunctions.Pressable(Scope.Entity);
        FocusFunctions.Focusable(Scope.Entity);
        bool focused = Scope.Entity.Focused();
        using (Absolute().Size(40, 24)
            .Background(On ? new Color(0.15f, 0.45f, 0.85f) : new Color(0.3f, 0.3f, 0.3f))
            .Corner(12).Border(new Color(0.7f, 0.8f, 1), focused ? 2 : 0).Content())
            Absolute().Size(16, 16).Position(On ? 20 : 4, 4).Background(new Color(1, 1, 1)).Corner(8);
        return default;
    }
}
