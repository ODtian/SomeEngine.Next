using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>A view that measures its content and places its measured children.</summary>
public interface ILayout : IView
{
    abstract Size IView.Measure(ref LayoutPass pass, in Constraints constraints);
}
