using SomeEngine.ECS.Components;

namespace SomeEngine.Views.Solid;

internal readonly record struct InsertedContent(Action Content) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Content();
        return default;
    }
}
