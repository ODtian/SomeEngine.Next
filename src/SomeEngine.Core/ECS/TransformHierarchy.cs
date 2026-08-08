using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Core.ECS;

/// <summary>
/// Transform-aware hierarchy operations. Ordinary <see cref="Hierarchy{TDomain}"/>
/// mutation deliberately preserves LocalTransform; these explicit operations preserve
/// the effective world transform instead.
/// </summary>
public static class TransformHierarchy<TDomain>
    where TDomain : IHierarchyDomain
{
    /// <summary>
    /// Reparents <paramref name="child"/> while preserving its fresh effective world transform.
    /// </summary>
    public static void SetParentInPlace(World world, Entity child, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireTransformBundle(world, child);

        TransformQvvs childWorld = ComputeWorldTransform(world, child);
        TransformQvvs parentWorld = ComputeWorldTransform(world, parent);
        Matrix4x4 childWorldMatrix = childWorld.ToMatrix();
        Matrix4x4 parentWorldMatrix = parentWorld.ToMatrix();
        if (!Matrix4x4.Invert(parentWorldMatrix, out Matrix4x4 inverseParentMatrix))
        {
            throw new InvalidOperationException(
                $"Cannot preserve world transform while parenting {child} to {parent}: " +
                "the effective parent transform is non-invertible.");
        }

        Matrix4x4 newLocalMatrix = childWorldMatrix * inverseParentMatrix;
        if (!TransformQvvs.TryCreateFromMatrix(newLocalMatrix, out TransformQvvs newLocal) ||
            !TransformQvvs.MatrixApproximatelyEquals(
                newLocal.ToMatrix() * parentWorldMatrix,
                childWorldMatrix))
        {
            throw new InvalidOperationException(
                $"Cannot preserve world transform while parenting {child} to {parent}: " +
                "the required LocalTransform contains shear or otherwise cannot be represented by QVVS.");
        }

        Parent<TDomain>? previousParent = world.Has<Parent<TDomain>>(child)
            ? world.Read<Parent<TDomain>>(child)
            : null;
        int? previousSiblingIndex = GetOrderedSiblingIndex(world, child, previousParent);
        LocalTransform previousLocal = world.Read<LocalTransform>(child);
        WorldTransform previousWorld = world.Read<WorldTransform>(child);

        Hierarchy<TDomain>.SetParent(world, child, parent);
        try
        {
            world.Replace(child, new LocalTransform { Value = newLocal });
            world.Replace(child, new WorldTransform { Qvvs = childWorld });
        }
        catch
        {
            Restore(
                world,
                child,
                previousParent,
                previousSiblingIndex,
                previousLocal,
                previousWorld);
            throw;
        }
    }

    /// <summary>
    /// Detaches <paramref name="child"/> while preserving its fresh effective world transform.
    /// </summary>
    public static void DetachInPlace(World world, Entity child)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireTransformBundle(world, child);

        TransformQvvs childWorld = ComputeWorldTransform(world, child);
        Parent<TDomain>? previousParent = world.Has<Parent<TDomain>>(child)
            ? world.Read<Parent<TDomain>>(child)
            : null;
        int? previousSiblingIndex = GetOrderedSiblingIndex(world, child, previousParent);
        LocalTransform previousLocal = world.Read<LocalTransform>(child);
        WorldTransform previousWorld = world.Read<WorldTransform>(child);

        Hierarchy<TDomain>.Detach(world, child);
        try
        {
            world.Replace(child, new LocalTransform { Value = childWorld });
            world.Replace(child, new WorldTransform { Qvvs = childWorld });
        }
        catch
        {
            Restore(
                world,
                child,
                previousParent,
                previousSiblingIndex,
                previousLocal,
                previousWorld);
            throw;
        }
    }

    /// <summary>
    /// Computes current world space directly from canonical Parent and LocalTransform values.
    /// It does not rely on a possibly stale WorldTransform component.
    /// </summary>
    public static TransformQvvs ComputeWorldTransform(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.IsAlive(entity))
            throw new InvalidOperationException($"Cannot compute transform for dead entity {entity}.");

        var chain = new List<Entity>();
        Entity current = entity;
        int remaining = world.EntityCount + 1;
        while (current != Entity.Null)
        {
            if (!world.IsAlive(current))
                throw new InvalidOperationException($"Transform hierarchy contains dead entity {current}.");
            if (remaining-- <= 0)
                throw new InvalidOperationException("Transform hierarchy contains a cycle.");

            chain.Add(current);
            current = Hierarchy<TDomain>.GetParent(world, current);
        }

        TransformQvvs result = TransformQvvs.Identity;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            Entity node = chain[i];
            bool hasLocal = world.Has<LocalTransform>(node);
            bool hasWorld = world.Has<WorldTransform>(node);
            if (hasLocal != hasWorld)
            {
                throw new InvalidOperationException(
                    $"Transform workload entity {node} must have LocalTransform and WorldTransform together.");
            }

            if (hasLocal)
            {
                TransformQvvs local = world.Read<LocalTransform>(node).Value;
                result = TransformQvvs.Combine(result, local);
            }
        }

        return result;
    }

    private static void RequireTransformBundle(World world, Entity entity)
    {
        if (!world.IsAlive(entity) ||
            !world.Has<LocalTransform>(entity) ||
            !world.Has<WorldTransform>(entity))
        {
            throw new InvalidOperationException(
                $"World-preserving hierarchy mutation requires {entity} to have " +
                "LocalTransform and WorldTransform.");
        }
    }

    private static void Restore(
        World world,
        Entity child,
        Parent<TDomain>? previousParent,
        int? previousSiblingIndex,
        LocalTransform previousLocal,
        WorldTransform previousWorld)
    {
        world.Replace(child, previousLocal);
        world.Replace(child, previousWorld);
        if (previousParent is { } parent)
        {
            if (previousSiblingIndex is { } index)
                Hierarchy<TDomain>.SetParent(world, child, parent.Value, index);
            else
                Hierarchy<TDomain>.SetParent(world, child, parent.Value);
        }
        else
            Hierarchy<TDomain>.Detach(world, child);
    }

    private static int? GetOrderedSiblingIndex(
        World world,
        Entity child,
        Parent<TDomain>? parent)
    {
        if (parent is not { } previousParent ||
            Hierarchy<TDomain>.GetChildOrderPolicy(world, previousParent.Value) != ChildOrderPolicy.Ordered)
        {
            return null;
        }

        ReadOnlySpan<Entity> siblings = Hierarchy<TDomain>
            .GetChildren(world, previousParent.Value)
            .Span;
        for (int index = 0; index < siblings.Length; index++)
        {
            if (siblings[index] == child)
                return index;
        }

        return null;
    }
}

/// <summary>Default-domain transform-aware hierarchy facade.</summary>
public static class TransformHierarchy
{
    public static void SetParentInPlace(World world, Entity child, Entity parent) =>
        TransformHierarchy<DefaultHierarchyDomain>.SetParentInPlace(world, child, parent);

    public static void DetachInPlace(World world, Entity child) =>
        TransformHierarchy<DefaultHierarchyDomain>.DetachInPlace(world, child);

    public static TransformQvvs ComputeWorldTransform(World world, Entity entity) =>
        TransformHierarchy<DefaultHierarchyDomain>.ComputeWorldTransform(world, entity);
}
