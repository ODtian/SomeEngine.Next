using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The current observation of one selected contact, in world units.</summary>
public readonly record struct ContactEventSample(Entity First, Entity Second, Vector3 WorldPoint, Vector3 Normal,
    float Separation, Vector3 Force);

public interface IContactEventSource
{
    QueryDefinition AddBodyReads(QueryDefinition definition) => definition;
    QueryDefinition AddReads(QueryDefinition definition) => definition;
    void AddReadResources(QueryReadResources resources) { }
    void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) { }
    bool TryRead(scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out ContactEventSample sample);
}

/// <summary>Samples the contact's original geometry, weighted anchors and solved response.</summary>
public readonly struct BodyContactEventSource : IContactEventSource
{
    public QueryDefinition AddBodyReads(QueryDefinition definition) => definition.WithAccess(
        new QueryDefinitionBuilder().Read<BodyPose>().Build());
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithAccess(new QueryDefinitionBuilder()
        .Read<ContactPoint>().Read<UndirectedRelationEndpoints<ContactPoint>>().ReadBuffer<ContactBodyLink>()
        .Optional<ContactResponse>(QueryAccess.Read).Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).Build());
    public bool TryRead(scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out ContactEventSample sample)
    {
        ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ContactPoint>>();
        Vector3 delta = ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), bodies, in context, false);
        float separation = point.Separation + Vector3.Dot(point.Normal, delta - point.BodyAnchorDelta);
        Vector3 force = row.TryReadRef<ContactResponse>(out var response) ? response.Value.ForceOnSecond : default;
        sample = new(endpoints.EndpointA, endpoints.EndpointB, point.WorldPoint, point.Normal, separation, force);
        return true;
    }
}
