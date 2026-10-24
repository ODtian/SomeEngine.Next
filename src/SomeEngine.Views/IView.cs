using SomeEngine.Core.Diagnostics;
using SomeEngine.Layout;

namespace SomeEngine.Views;

/// <summary>A node whose fields supply its parameters and whose build declares its content.</summary>
public interface IView
{
    /// <summary>Declares the node's own data and child views in the active scope.</summary>
    BuildTask Build();
    /// <summary>The measurement entry used by views implementing ILayout.</summary>
    Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        DevelopmentCheck.Fail("Layout measurement requires an ILayout view.");
        return default;
    }
    /// <summary>Declares how later parameter and dependency changes affect this view.</summary>
    static virtual BuildMode Mode => BuildMode.Rerun;
    /// <summary>Declares whether this build or its caller supplies the children.</summary>
    static virtual ChildSource Children => ChildSource.Build;
}

/// <summary>The view's declared response to changes after its first build.</summary>
public enum BuildMode : byte
{
    Rerun,
    Once,
}

/// <summary>The part of the interface that declares a view's children.</summary>
public enum ChildSource : byte
{
    Build,
    Caller,
}
