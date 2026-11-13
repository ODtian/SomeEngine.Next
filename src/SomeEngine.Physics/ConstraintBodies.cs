using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>Directed native links from a variable-body constraint to its bodies, in endpoint order.</summary>
[RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
public readonly struct ConstraintBodyLink : ITag;

public static class ConstraintBodies
{
    public static Entity Create<TConstraint>(World world, in TConstraint constraint, ReadOnlySpan<Entity> bodies)
        where TConstraint : struct, IComponent
    {
        using var edit = world.OpenStructureWrite();
        Entity entity = world.Create(constraint);
        Add(world, entity, bodies);
        return entity;
    }

    public static void Add(World world, Entity constraint, ReadOnlySpan<Entity> bodies)
    {
        Add(world, [constraint], [0, bodies.Length], bodies);
    }

    /// <summary>Creates all endpoint links in one native topology batch; offsets delimit each constraint's bodies.</summary>
    public static void Add(World world, ReadOnlySpan<Entity> constraints, ReadOnlySpan<int> offsets, ReadOnlySpan<Entity> bodies)
    {
        using var edit = world.OpenStructureWrite();
        var changes = new EndpointEdit[bodies.Length];
        var created = new RelationEdge<ConstraintBodyLink>[bodies.Length];
        for (int constraint = 0; constraint < constraints.Length; constraint++)
        {
            Relations<ConstraintBodyLink>.SetOrdered(world, constraints[constraint], RelationAdjacencyRole.Outgoing, true);
            for (int body = offsets[constraint]; body < offsets[constraint + 1]; body++)
                changes[body] = EndpointEdit.Create(constraints[constraint], bodies[body]);
        }
        Relations<ConstraintBodyLink>.Edit(world, changes, created);
    }

    public static RelationRead GetBodies(World world, Entity constraint) =>
        Relations<ConstraintBodyLink>.Outgoing(world, constraint);

    public static Entity GetBody(World world, Entity constraint, int index)
    {
        using RelationRead bodies = GetBodies(world, constraint);
        return bodies.Entries[index].Other;
    }
}
