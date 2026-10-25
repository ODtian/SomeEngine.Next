using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>Finite-stiffness relation payload; ECS holds its two bodies in UndirectedRelationEndpoints.</summary>
[RelationSchema(RelationDirection.Undirected)]
public struct ElasticConnection : IComponent
{
    public Vector3 FirstAnchor;
    public Vector3 SecondAnchor;
    public Quaternion RestRotation;
    public float LinearStiffness;
    public float AngularStiffness;
}

/// <summary>ALM forces, penalty metrics and initial errors stored on the connection entity.</summary>
public struct AvbdElasticState : IComponent
{
    public Vector3 LinearMultiplier;
    public Vector3 AngularMultiplier;
    public float LinearPenalty;
    public float AngularPenalty;
    public Vector3 InitialLinearError;
    public Vector3 InitialAngularError;
}
