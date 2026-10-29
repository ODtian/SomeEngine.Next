using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class SiloMotionMeasurements
{
    private readonly ParticleMotion[] _particles;
    private readonly int[] _shapes = new int[3], _crossedOutlet = new int[3], _discharged = new int[3];
    private readonly Vector2 _containerMinimum, _containerMaximum, _outletCenter, _outletHalfExtents;
    private readonly float _outletHeight;
    private readonly long _lastStep;
    private long _completedStep = -1, _closedStep = -1;
    private int _closedContained, _closedDescended, _closedContacts, _outsideOutletCrossings, _dischargedStaticContacts;
    private double _closedEnergy;

    internal SiloMotionMeasurements(World world, PhysicsSceneSimulation scene, long lastStep)
    {
        ReadOnlySpan<Entity> bodies = scene.Bodies.Span;
        _particles = new ParticleMotion[bodies.Length - PhysicsSiloScenes.StaticBodies];
        _lastStep = lastStep;
        _containerMinimum = new(
            world.Read<BodyPose>(bodies[5]).Position.X + world.Read<BoxGeometry>(bodies[5]).HalfExtents.X,
            world.Read<BodyPose>(bodies[7]).Position.Y + world.Read<BoxGeometry>(bodies[7]).HalfExtents.Y);
        _containerMaximum = new(
            world.Read<BodyPose>(bodies[6]).Position.X - world.Read<BoxGeometry>(bodies[6]).HalfExtents.X,
            world.Read<BodyPose>(bodies[8]).Position.Y - world.Read<BoxGeometry>(bodies[8]).HalfExtents.Y);
        BodyPose gate = world.Read<BodyPose>(bodies[0]);
        Vector3 halfExtents = world.Read<BoxGeometry>(bodies[0]).HalfExtents;
        _outletCenter = new(gate.Position.X, gate.Position.Y);
        _outletHalfExtents = new(halfExtents.X, halfExtents.Y);
        _outletHeight = gate.Position.Z + halfExtents.Z;
    }

    internal void Initialize(int bodyIndex, Vector3 center)
    {
        int index = bodyIndex - PhysicsSiloScenes.StaticBodies;
        _particles[index] = new() { InitialHeight = center.Z, PreviousCenter = center };
        _shapes[index % 3]++;
    }

    internal void BeginRead(long number)
    {
        _completedStep = number;
        Array.Clear(_discharged);
        if (number < PhysicsSiloScenes.OpenStep) _closedContained = _closedDescended = 0;
    }

    internal void Read(long number, int bodyIndex, Vector3 center)
    {
        int index = bodyIndex - PhysicsSiloScenes.StaticBodies;
        ref ParticleMotion particle = ref _particles[index];
        if (number < PhysicsSiloScenes.OpenStep)
        {
            if (center.X >= _containerMinimum.X && center.X <= _containerMaximum.X
                && center.Y >= _containerMinimum.Y && center.Y <= _containerMaximum.Y && center.Z >= _outletHeight)
                _closedContained++;
            if (center.Z < particle.InitialHeight) _closedDescended++;
        }
        else if (particle.PreviousCenter.Z >= _outletHeight && center.Z < _outletHeight)
        {
            float fraction = (particle.PreviousCenter.Z - _outletHeight) / (particle.PreviousCenter.Z - center.Z);
            Vector3 crossing = Vector3.Lerp(particle.PreviousCenter, center, fraction);
            if (MathF.Abs(crossing.X - _outletCenter.X) <= _outletHalfExtents.X
                && MathF.Abs(crossing.Y - _outletCenter.Y) <= _outletHalfExtents.Y)
            {
                if (!particle.CrossedOutlet) _crossedOutlet[index % 3]++;
                particle.CrossedOutlet = true;
            }
            else _outsideOutletCrossings++;
        }
        if (particle.CrossedOutlet && center.Z < _outletHeight - 2) _discharged[index % 3]++;
        particle.PreviousCenter = center;
    }

    internal void CompleteRead(long number, int contacts, double energy)
    {
        if (number >= PhysicsSiloScenes.OpenStep) return;
        _closedStep = number; _closedContacts = contacts; _closedEnergy = energy;
    }

    internal bool IsFinalStep(long number) => number == _lastStep;

    internal void ReadContacts(scoped QueryCursor rows)
    {
        foreach (QueryRow row in rows.Rows)
        {
            if (!row.TryReadRef<ContactPoint>(out _)) continue;
            bool hasDischarged = false, hasStatic = false;
            foreach (ref readonly ContactBodyLink link in row.ReadBuffer<ContactBodyLink>().AsSpan())
            {
                rows.TryGetRow(link.Participant.Entity, out QueryRow participant);
                Entity body = participant.ReadRef<DirectedRelationEndpoints<ContactParticipant>>().Target;
                rows.TryGetRow(body, out QueryRow bodyRow);
                if (bodyRow.ReadRef<BodyMass>().Motion == BodyMotion.Static) hasStatic = true;
                else
                {
                    int index = bodyRow.ReadRef<DeviceBodyBinding>().Index - PhysicsSiloScenes.StaticBodies;
                    hasDischarged |= _particles[index].CrossedOutlet
                        && bodyRow.ReadRef<BodyPose>().Position.Z < _outletHeight - 2;
                }
            }
            if (hasDischarged && hasStatic) _dischargedStaticContacts++;
        }
    }

    internal bool MeetsBehavior(double initialEnergy) => _completedStep == _lastStep
        && _closedStep == PhysicsSiloScenes.OpenStep - 1 && _closedContained == _particles.Length
        && _closedDescended == _particles.Length && _closedContacts > 0 && _closedEnergy <= initialEnergy
        && _outsideOutletCrossings == 0 && _discharged.All(count => count > 0) && _dischargedStaticContacts == 0;

    internal JsonObject Describe() => new()
    {
        ["Scope"] = "Every silo particle in the completed body query",
        ["ShapeOrder"] = "Original Sphere, Box, Hull sequence through DeviceBodyBinding.Index",
        ["ClosedGateStep"] = _closedStep, ["ClosedGateParticlesInContainer"] = _closedContained,
        ["ClosedGateParticlesBelowInitialHeight"] = _closedDescended, ["ClosedGateContactPoints"] = _closedContacts,
        ["ClosedGateMechanicalEnergyJoules"] = _closedEnergy,
        ["OutletHeightMeters"] = _outletHeight,
        ["OutletCenter"] = new JsonArray(_outletCenter.X, _outletCenter.Y),
        ["OutletHalfExtents"] = new JsonArray(_outletHalfExtents.X, _outletHalfExtents.Y),
        ["CrossingMethod"] = "Interpolate every completed center segment through the original gate plane",
        ["OutsideOutletPlaneCrossings"] = _outsideOutletCrossings,
        ["CurrentStaticContactsOfDischargedParticles"] = _dischargedStaticContacts,
        ["Sphere"] = DescribeShape(0), ["Box"] = DescribeShape(1), ["Hull"] = DescribeShape(2),
    };

    private JsonObject DescribeShape(int index) => new()
    {
        ["Particles"] = _shapes[index], ["CrossedOutlet"] = _crossedOutlet[index],
        ["ThroughOutletAndBelowTwoMetres"] = _discharged[index],
    };

    private struct ParticleMotion
    {
        internal float InitialHeight;
        internal Vector3 PreviousCenter;
        internal bool CrossedOutlet;
    }
}
