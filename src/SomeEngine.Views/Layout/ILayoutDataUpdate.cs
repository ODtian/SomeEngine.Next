using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Layout;

/// <summary>Updates content data with the node's current content constraints before its measurement.</summary>
public interface ILayoutDataUpdate
{
    void Update(World world, Entity node, in Constraints constraints);

    /// <summary>Commits the final content results after the canvas has been measured and placed.</summary>
    void Complete(World world) { }
}
