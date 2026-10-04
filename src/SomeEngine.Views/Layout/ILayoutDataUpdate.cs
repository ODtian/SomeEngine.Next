using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Layout;

/// <summary>Updates content data with the node's current content constraints before its measurement.</summary>
public interface ILayoutDataUpdate
{
    /// <summary>Whether content updates can run from detached data during subtree measurement.</summary>
    bool SupportsParallel => false;

    /// <summary>Reads content on the system thread before subtree jobs begin.</summary>
    ILayoutContent? ReadContent(World world, Entity node) => null;

    /// <summary>Reports content input changes that require a fresh layout measurement.</summary>
    bool HasChanges(World world, Entity node) => true;

    void Update(World world, Entity node, in Constraints constraints);

    /// <summary>Commits final content results after all canvases have been measured and placed.</summary>
    void Complete(World world) { }
}

/// <summary>Node content held outside ECS, updated with the measured content constraints.</summary>
public interface ILayoutContent
{
    void Update(in Constraints constraints);
}
