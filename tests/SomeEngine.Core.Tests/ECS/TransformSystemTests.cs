using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Core.Tests.ECS;

public class TransformSystemTests
{
    private readonly GameWorld _world = new();

    private void RunSystems()
        => _world.Update(0);

    [Fact]
    public void TestHierarchyAndTransform()
    {
        Entity root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, root);

        Entity grandChild = _world.World.CreateEntity();
        _world.World.Add(grandChild, new LocalTransform { Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity) });
        _world.World.Add(grandChild, new WorldTransform());
        Hierarchy.SetParent(_world.World, grandChild, child);

        RunSystems();

        var rootWorld = _world.World.Read<WorldTransform>(root).Qvvs;
        var childWorld = _world.World.Read<WorldTransform>(child).Qvvs;
        var grandChildWorld = _world.World.Read<WorldTransform>(grandChild).Qvvs;

        Assert.Equal(new Vector3(0, 0, 0), rootWorld.Position);
        Assert.Equal(new Vector3(10, 0, 0), childWorld.Position);
        Assert.Equal(new Vector3(10, 5, 0), grandChildWorld.Position);
    }

    [Fact]
    public void TestRotation()
    {
        Entity root = _world.World.CreateEntity();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, rotation) });
        _world.World.Add(root, new WorldTransform());

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, root);

        RunSystems();

        var childWorld = _world.World.Read<WorldTransform>(child).Qvvs;
        var expected = Vector3.Transform(new Vector3(10, 0, 0), rotation);

        Assert.InRange(childWorld.Position.X, expected.X - 0.001f, expected.X + 0.001f);
        Assert.InRange(childWorld.Position.Y, expected.Y - 0.001f, expected.Y + 0.001f);
        Assert.InRange(childWorld.Position.Z, expected.Z - 0.001f, expected.Z + 0.001f);
    }

    [Fact]
    public void OrganizationEntity_PassesNearestTransformAncestorToDescendants()
    {
        Entity root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(root, new WorldTransform());

        Entity organization = _world.World.CreateEntity();
        Hierarchy.SetParent(_world.World, organization, root);

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(2, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, organization);

        RunSystems();

        Assert.Equal(
            new Vector3(12, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void OrganizationEntity_ReparentUpdatesDescendantAfterSteadyFrame()
    {
        Entity rootA = CreateTransformEntity(new Vector3(10, 0, 0));
        Entity rootB = CreateTransformEntity(new Vector3(100, 0, 0));
        Entity organization = _world.World.CreateEntity();
        Entity child = CreateTransformEntity(new Vector3(2, 0, 0));

        Hierarchy.SetParent(_world.World, organization, rootA);
        Hierarchy.SetParent(_world.World, child, organization);
        RunSystems();
        RunSystems();

        Assert.Equal(
            new Vector3(12, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);

        Hierarchy.SetParent(_world.World, organization, rootB);
        RunSystems();

        Assert.Equal(
            new Vector3(102, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void OrganizationEntity_DetachUpdatesDescendantAfterSteadyFrame()
    {
        Entity root = CreateTransformEntity(new Vector3(10, 0, 0));
        Entity organization = _world.World.CreateEntity();
        Entity child = CreateTransformEntity(new Vector3(2, 0, 0));

        Hierarchy.SetParent(_world.World, organization, root);
        Hierarchy.SetParent(_world.World, child, organization);
        RunSystems();
        RunSystems();

        Assert.Equal(
            new Vector3(12, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);

        Hierarchy.Detach(_world.World, organization);
        RunSystems();

        Assert.Equal(
            new Vector3(2, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void NestedOrganizationEntity_ReparentUpdatesDescendantAfterSteadyFrame()
    {
        Entity rootA = CreateTransformEntity(new Vector3(10, 0, 0));
        Entity rootB = CreateTransformEntity(new Vector3(100, 0, 0));
        Entity outerOrganization = _world.World.CreateEntity();
        Entity innerOrganization = _world.World.CreateEntity();
        Entity child = CreateTransformEntity(new Vector3(2, 0, 0));

        Hierarchy.SetParent(_world.World, outerOrganization, rootA);
        Hierarchy.SetParent(_world.World, innerOrganization, outerOrganization);
        Hierarchy.SetParent(_world.World, child, innerOrganization);
        RunSystems();
        RunSystems();

        Hierarchy.SetParent(_world.World, outerOrganization, rootB);
        RunSystems();

        Assert.Equal(
            new Vector3(102, 0, 0),
            _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void SetParentInPlace_PreservesFreshWorldTransform()
    {
        Entity oldParent = _world.World.CreateEntity();
        _world.World.Add(oldParent, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(oldParent, new WorldTransform());

        Entity newParent = _world.World.CreateEntity();
        _world.World.Add(newParent, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(100, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(newParent, new WorldTransform());

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(2, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, oldParent);
        RunSystems();

        TransformHierarchy.SetParentInPlace(_world.World, child, newParent);

        Assert.Equal(newParent, Hierarchy.GetParent(_world.World, child));
        Assert.Equal(new Vector3(-88, 0, 0), _world.World.Read<LocalTransform>(child).Value.Position);
        Assert.Equal(new Vector3(12, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);

        RunSystems();
        Assert.Equal(new Vector3(12, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void SetParentInPlace_NonInvertibleParentLeavesTopologyAndLocalUnchanged()
    {
        Entity oldParent = _world.World.CreateEntity();
        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(2, 0, 0), Quaternion.Identity),
        });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, oldParent);

        Entity degenerateParent = _world.World.CreateEntity();
        _world.World.Add(degenerateParent, new LocalTransform
        {
            Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity, scale: 0.0f),
        });
        _world.World.Add(degenerateParent, new WorldTransform());

        LocalTransform before = _world.World.Read<LocalTransform>(child);
        Assert.Throws<InvalidOperationException>(
            () => TransformHierarchy.SetParentInPlace(_world.World, child, degenerateParent));

        Assert.Equal(oldParent, Hierarchy.GetParent(_world.World, child));
        Assert.Equal(before.Value.Position, _world.World.Read<LocalTransform>(child).Value.Position);
    }

    [Fact]
    public void SetParentInPlace_RotatedNonUniformParentPreservesRepresentableWorldTransform()
    {
        TransformQvvs parentTransform = new(new Vector3(7, -3, 5),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f),
            scale: 1.25f)
        {
            Stretch = new Vector3(2, 3, 4),
        };
        TransformQvvs intendedLocal = new(new Vector3(2, -1, 0.5f), Quaternion.Identity)
        {
            Stretch = new Vector3(0.5f, 2, 0.25f),
        };
        TransformQvvs childWorld = TransformQvvs.Combine(parentTransform, intendedLocal);

        Entity parent = CreateTransformEntity(parentTransform);
        Entity child = CreateTransformEntity(childWorld);
        RunSystems();

        TransformHierarchy.SetParentInPlace(_world.World, child, parent);

        Assert.Equal(parent, Hierarchy.GetParent(_world.World, child));
        AssertMatrixNear(
            intendedLocal.ToMatrix(),
            _world.World.Read<LocalTransform>(child).Value.ToMatrix());
        AssertMatrixNear(
            childWorld.ToMatrix(),
            _world.World.Read<WorldTransform>(child).Qvvs.ToMatrix());

        RunSystems();
        AssertMatrixNear(
            childWorld.ToMatrix(),
            _world.World.Read<WorldTransform>(child).Qvvs.ToMatrix());
    }

    [Fact]
    public void SetParentInPlace_UnrepresentableShearLeavesAllPublishedStateUnchanged()
    {
        Entity oldParent = _world.World.CreateEntity();
        Hierarchy.SetChildOrderPolicy(_world.World, oldParent, ChildOrderPolicy.Ordered);
        Entity first = _world.World.CreateEntity();
        Entity child = CreateTransformEntity(TransformQvvs.Identity);
        Entity last = _world.World.CreateEntity();
        Hierarchy.SetParent(_world.World, first, oldParent);
        Hierarchy.SetParent(_world.World, child, oldParent);
        Hierarchy.SetParent(_world.World, last, oldParent);

        TransformQvvs parentTransform = new(new Vector3(4, -2, 1),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f))
        {
            Stretch = new Vector3(2, 3, 1),
        };
        Entity newParent = CreateTransformEntity(parentTransform);
        RunSystems();

        LocalTransform previousLocal = _world.World.Read<LocalTransform>(child);
        WorldTransform previousWorld = _world.World.Read<WorldTransform>(child);
        Entity[] previousChildren = Hierarchy.GetChildren(_world.World, oldParent).ToArray();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TransformHierarchy.SetParentInPlace(_world.World, child, newParent));

        Assert.Contains("cannot be represented by QVVS", error.Message);
        Assert.Equal(oldParent, Hierarchy.GetParent(_world.World, child));
        Assert.Equal(previousChildren, Hierarchy.GetChildren(_world.World, oldParent).ToArray());
        Assert.Empty(Hierarchy.GetChildren(_world.World, newParent));
        AssertMatrixNear(
            previousLocal.Value.ToMatrix(),
            _world.World.Read<LocalTransform>(child).Value.ToMatrix());
        AssertMatrixNear(
            previousWorld.Qvvs.ToMatrix(),
            _world.World.Read<WorldTransform>(child).Qvvs.ToMatrix());
    }

    [Fact]
    public void SetParentInPlace_ComponentFaultRestoresOrderedSiblingIndexAndTransforms()
    {
        Entity oldParent = CreateTransformEntity(new TransformQvvs(
            new Vector3(10, 0, 0),
            Quaternion.Identity));
        Hierarchy.SetChildOrderPolicy(_world.World, oldParent, ChildOrderPolicy.Ordered);
        Entity newParent = CreateTransformEntity(new TransformQvvs(
            new Vector3(100, 0, 0),
            Quaternion.Identity));
        Entity first = _world.World.CreateEntity();
        Entity child = CreateTransformEntity(new TransformQvvs(
            new Vector3(2, 0, 0),
            Quaternion.Identity));
        Entity last = _world.World.CreateEntity();
        Hierarchy.SetParent(_world.World, first, oldParent);
        Hierarchy.SetParent(_world.World, child, oldParent);
        Hierarchy.SetParent(_world.World, last, oldParent);
        RunSystems();

        LocalTransform previousLocal = _world.World.Read<LocalTransform>(child);
        WorldTransform previousWorld = _world.World.Read<WorldTransform>(child);
        bool throwOnce = true;
        _world.World.Hooks<LocalTransform>().OnReplace(
            (DeferredWorld _, Entity entity, in LocalTransform _) =>
            {
                if (entity == child && throwOnce)
                {
                    throwOnce = false;
                    throw new InvalidOperationException("injected LocalTransform hook fault");
                }
            });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TransformHierarchy.SetParentInPlace(_world.World, child, newParent));

        Assert.Equal("injected LocalTransform hook fault", error.Message);
        Assert.Equal(oldParent, Hierarchy.GetParent(_world.World, child));
        Assert.Equal(
            new[] { first, child, last },
            Hierarchy.GetChildren(_world.World, oldParent).ToArray());
        Assert.Empty(Hierarchy.GetChildren(_world.World, newParent));
        AssertMatrixNear(
            previousLocal.Value.ToMatrix(),
            _world.World.Read<LocalTransform>(child).Value.ToMatrix());
        AssertMatrixNear(
            previousWorld.Qvvs.ToMatrix(),
            _world.World.Read<WorldTransform>(child).Qvvs.ToMatrix());
    }

    [Fact]
    public void TryInverse_RotatedNonUniformStretchRejectsUnrepresentableInverse()
    {
        TransformQvvs transform = new(new Vector3(1, 2, 3),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f))
        {
            Stretch = new Vector3(2, 3, 4),
        };

        Assert.False(transform.TryInverse(out _));
        Assert.Throws<InvalidOperationException>(() => transform.Inverse());
    }

    [Fact]
    public void TryInverse_UniformStretchReconstructsIdentity()
    {
        TransformQvvs transform = new(new Vector3(1, 2, 3),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f),
            scale: 2.0f);

        Assert.True(transform.TryInverse(out TransformQvvs inverse));
        AssertMatrixNear(Matrix4x4.Identity, transform.ToMatrix() * inverse.ToMatrix());
        AssertMatrixNear(Matrix4x4.Identity, inverse.ToMatrix() * transform.ToMatrix());
    }

    [Fact]
    public void ParentMove_UpdatesDescendantWorldTransformAfterSteadyFrame()
    {
        Entity root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, root);

        RunSystems();
        RunSystems();

        LocalTransform rootLocal = _world.World.Read<LocalTransform>(root);
        rootLocal.Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity);
        _world.World.Replace(root, rootLocal);

        RunSystems();

        Assert.Equal(new Vector3(11, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void RemoveParent_UpdatesChildWorldTransform()
    {
        Entity root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        Entity child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(_world.World, child, root);

        RunSystems();
        Assert.Equal(new Vector3(11, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);

        Hierarchy.Detach(_world.World, child);
        RunSystems();

        Assert.Equal(new Vector3(1, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void TestRepeatedUpdatesDoNotExhaustJobCounters()
    {
        Entity root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        for (int i = 0; i < 200; i++)
            RunSystems();

        var world = _world.World.Read<WorldTransform>(root).Qvvs;
        Assert.Equal(Vector3.Zero, world.Position);
    }

    [Fact]
    public void FlatLeafUpdate_OnlyMarksChangedWorldTransformRows()
    {
        Entity first = _world.World.CreateEntity();
        _world.World.Add(first, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(first, new WorldTransform());

        Entity second = _world.World.CreateEntity();
        _world.World.Add(second, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(second, new WorldTransform());

        RunSystems();
        RunSystems();

        uint beforeLocalChange = _world.World.CurrentTick;
        _world.World.Replace(first, new LocalTransform { Value = new TransformQvvs(new Vector3(5, 0, 0), Quaternion.Identity) });

        RunSystems();

        QueryHandle changedWorldTransforms = _world.World.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Changed<WorldTransform>()
                .None<Parent<DefaultHierarchyDomain>>()
                .None<Children<DefaultHierarchyDomain>>());

        List<Entity> changed = [];
        _world.World.ExecuteQuery(
            changedWorldTransforms,
            beforeLocalChange,
            cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                foreach (int row in chunk.RowIndices)
                    changed.Add(chunk.GetEntity(row));
            }
        });

        Entity changedEntity = Assert.Single(changed);
        Assert.Equal(first, changedEntity);
        Assert.DoesNotContain(second, changed);
    }

    private Entity CreateTransformEntity(Vector3 position)
    {
        return CreateTransformEntity(new TransformQvvs(position, Quaternion.Identity));
    }

    private Entity CreateTransformEntity(TransformQvvs transform)
    {
        Entity entity = _world.World.CreateEntity();
        _world.World.Add(entity, new LocalTransform
        {
            Value = transform,
        });
        _world.World.Add(entity, new WorldTransform());
        return entity;
    }

    private static void AssertMatrixNear(in Matrix4x4 expected, in Matrix4x4 actual)
    {
        AssertVectorNear(
            Vector3.Transform(Vector3.Zero, expected),
            Vector3.Transform(Vector3.Zero, actual));
        AssertVectorNear(
            Vector3.Transform(Vector3.UnitX, expected),
            Vector3.Transform(Vector3.UnitX, actual));
        AssertVectorNear(
            Vector3.Transform(Vector3.UnitY, expected),
            Vector3.Transform(Vector3.UnitY, actual));
        AssertVectorNear(
            Vector3.Transform(Vector3.UnitZ, expected),
            Vector3.Transform(Vector3.UnitZ, actual));
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual)
    {
        const float tolerance = 1.0e-4f;
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
