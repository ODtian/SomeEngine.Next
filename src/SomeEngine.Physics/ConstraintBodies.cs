using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>One protected constraint-to-body participation occurrence.</summary>
[RelationSchema(RelationDirection.Directed)]
public struct ConstraintParticipant : IComponent;

/// <summary>The original participation edge; buffer order supplies the constraint's endpoint roles.</summary>
[BufferCapacity(4)]
public readonly record struct ConstraintBodyLink(RelationEdge<ConstraintParticipant> Participant) : IBufferElement;

public static class ConstraintBodies
{
    public static Entity Create<TConstraint>(World world, in TConstraint constraint, ReadOnlySpan<Entity> bodies)
        where TConstraint : struct, IComponent
    {
        Entity entity = world.CreateEntity(constraint);
        Add(world, entity, bodies);
        return entity;
    }

    public static void Add(World world, Entity constraint, ReadOnlySpan<Entity> bodies)
    {
        ConstraintBodyLink[] links = ArrayPool<ConstraintBodyLink>.Shared.Rent(bodies.Length);
        try
        {
            for (int index = 0; index < bodies.Length; index++)
                links[index] = new(world.CreateRelation(constraint, bodies[index], new ConstraintParticipant()));
            world.AddBuffer<ConstraintBodyLink>(constraint);
            var values = (Links: links, Count: bodies.Length);
            world.ExecuteBufferWrite<ConstraintBodyLink, (ConstraintBodyLink[] Links, int Count)>(constraint, ref values,
                static (buffer, ref values) =>
                {
                    foreach (ConstraintBodyLink link in values.Links.AsSpan(0, values.Count)) buffer.Add(link);
                });
        }
        finally { ArrayPool<ConstraintBodyLink>.Shared.Return(links); }
    }

    public static QueryDefinition AddReads(QueryDefinition definition) => definition.WithAccess(
        new QueryDefinitionBuilder().ReadBuffer<ConstraintBodyLink>()
            .Optional<DirectedRelationEndpoints<ConstraintParticipant>>(QueryAccess.Read).Build());

    public static Entity GetBody(in ConstraintBodyLink link, in PhysicsStepContext context) =>
        context.World.GetDirectedRelationEndpoints(link.Participant).Target;

    public static Entity GetBody(in ConstraintBodyLink link, scoped PhysicsBodies bodies, in PhysicsStepContext context) =>
        bodies.TryReadReference<DirectedRelationEndpoints<ConstraintParticipant>>(link.Participant.Entity, out var endpoints)
            ? endpoints.Value.Target : GetBody(in link, in context);

    public static Entity GetBody(in ConstraintBodyLink link, scoped QueryCursor access)
    {
        access.TryGetRow(link.Participant.Entity, out QueryRow row);
        return row.ReadRef<DirectedRelationEndpoints<ConstraintParticipant>>().Target;
    }
}
