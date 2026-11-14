namespace SomeEngine.ECS.Components;

/// <summary>
/// Marks a partial struct whose fields form one named set of components. The bundle generator implements
/// <see cref="IComponentBundle{TSelf}"/> for it; nested bundles are flattened and do not create a runtime hierarchy.
/// </summary>
public interface IComponentBundle { }

/// <summary>The component layout and field writes of a named bundle, implemented by the bundle generator.</summary>
public interface IComponentBundle<TSelf> : IComponentBundle where TSelf : struct, IComponentBundle<TSelf>
{
    /// <summary>Table, tag, shared and buffer component numbers of the bundle's archetype.</summary>
    static abstract ReadOnlySpan<int> ComponentIds { get; }
    /// <summary>Sparse component numbers written after the entity is placed.</summary>
    static abstract ReadOnlySpan<int> SparseComponentIds { get; }
    static abstract void Write(BundleWriteView view, in TSelf bundle);
}
