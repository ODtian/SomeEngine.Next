using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Original body fields borrowed by a parent Job until its numerical ranges complete.</summary>
public readonly unsafe ref struct PhysicsBodies
{
    private readonly BodyColumns _columns;
    internal PhysicsBodies(BodyColumns columns) => _columns = columns;
    internal BodyColumns Columns => _columns;
    internal ReadOnlySpan<Entity> Entities => _columns.Entities;
    public bool Contains(Entity entity) => _columns.Contains(entity);
    public bool HasActivity(Entity entity) => Fields(entity).Activity != null;
    public bool IsAwake(Entity entity) => !HasActivity(entity) || !ReadActivity(entity).Sleeping;
    public ref readonly BodyPose ReadPose(Entity entity) => ref *Fields(entity).Pose;
    public ref readonly BodyMass ReadMass(Entity entity) => ref *Fields(entity).Mass;
    public ref readonly BodyVelocity ReadVelocity(Entity entity) => ref *Fields(entity).Velocity;
    public ref readonly BodyStepState ReadStepState(Entity entity) => ref *Fields(entity).Step;
    public ref readonly BodyActivity ReadActivity(Entity entity) => ref *Fields(entity).Activity;
    public ref readonly AvbdBodyEquation ReadEquation(Entity entity) => ref *Fields(entity).Equation;
    public ref BodyPose WritePose(Entity entity) { Require(entity, BodyWrites.Pose); return ref *Fields(entity).Pose; }
    public ref BodyVelocity WriteVelocity(Entity entity) { Require(entity, BodyWrites.Velocity); return ref *Fields(entity).Velocity; }
    public ref BodyStepState WriteStepState(Entity entity) { Require(entity, BodyWrites.Step); return ref *Fields(entity).Step; }
    public ref BodyActivity WriteActivity(Entity entity) { Require(entity, BodyWrites.Activity); return ref *Fields(entity).Activity; }
    public ref AvbdBodyEquation WriteEquation(Entity entity) { Require(entity, BodyWrites.Equation); return ref *Fields(entity).Equation; }
    private ref readonly BodyColumns.Fields Fields(Entity entity) => ref _columns.Get(entity);
    private void Require(Entity entity, BodyWrites field) => DevelopmentCheck.Require((Fields(entity).Writes & field) != 0,
        "Declare the body's field write in the parent numerical Job.");
}
