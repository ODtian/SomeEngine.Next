using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodies
{
    private readonly BodyInputVersions[] _cpuVersions;
    [Flags]
    internal enum BodyInputComponents : byte { Pose = 1, Mass = 2, Velocity = 4, Step = 8, All = 15 }
    internal readonly record struct BodyInputVersions(Entity Entity, uint Pose, uint Mass, uint Velocity, uint Step,
        BodyInputComponents Known);

    internal BodyInputVersions ReadCpuVersions(int index) => _cpuVersions[index];
    internal void AcceptCpuVersions(int index, in BodyInputVersions version) => _cpuVersions[index] = version;
    internal void AcceptCpuResult(int index, scoped QueryRow row)
    {
        BodyInputVersions previous = _cpuVersions[index];
        bool same = previous.Entity == row.Entity;
        _cpuVersions[index] = new(row.Entity, row.WriteVersion<BodyPose>(), same ? previous.Mass : 0,
            row.WriteVersion<BodyVelocity>(), row.WriteVersion<BodyStepState>(),
            BodyInputComponents.Pose | BodyInputComponents.Velocity | BodyInputComponents.Step
                | (same ? previous.Known & BodyInputComponents.Mass : 0));
    }
    internal JobResourceAccess GetCpuVersionsAccess(Scheduler scheduler) => JobResourceAccess.Write(scheduler, _cpuVersions);
    internal static BodyInputVersions ReadVersions(scoped QueryRow row) => new(row.Entity,
        row.WriteVersion<BodyPose>(), row.WriteVersion<BodyMass>(), row.WriteVersion<BodyVelocity>(),
        row.WriteVersion<BodyStepState>(), BodyInputComponents.All);

    internal static BodyInputComponents ChangedInputs(in BodyInputVersions current, in BodyInputVersions previous)
    {
        if (current.Entity != previous.Entity) return BodyInputComponents.All;
        BodyInputComponents changed = BodyInputComponents.All & ~previous.Known;
        if (current.Pose != previous.Pose) changed |= BodyInputComponents.Pose;
        if (current.Mass != previous.Mass) changed |= BodyInputComponents.Mass;
        if (current.Velocity != previous.Velocity) changed |= BodyInputComponents.Velocity;
        if (current.Step != previous.Step) changed |= BodyInputComponents.Step;
        return changed;
    }

    internal void WriteInput(scoped QueryRow row, int index)
    {
        ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
        ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
        ref readonly BodyVelocity velocity = ref row.ReadRef<BodyVelocity>();
        ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
        DeviceBodyState state = ReadInput(row.Entity, in pose, in mass, in velocity, in step);
        WriteInput(index, in state);
    }

    internal static DeviceBodyState ReadInput(Entity entity, in BodyPose pose, in BodyMass mass,
        in BodyVelocity velocity, in BodyStepState step) => new()
        {
            Position = pose.Position, Rotation = pose.Rotation, Motion = (uint)mass.Motion,
            LinearVelocity = velocity.Linear, AngularVelocity = velocity.Angular, InverseMass = mass.InverseMass,
            CenterOfMass = mass.CenterOfMass, EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation,
            InverseInertiaX = new(mass.InverseInertia.M11, mass.InverseInertia.M12, mass.InverseInertia.M13),
            InverseInertiaY = new(mass.InverseInertia.M21, mass.InverseInertia.M22, mass.InverseInertia.M23),
            InverseInertiaZ = new(mass.InverseInertia.M31, mass.InverseInertia.M32, mass.InverseInertia.M33),
            StartCenterOfMass = step.StartCenterOfMass, StartRotation = step.StartRotation,
            PredictedCenterOfMass = step.PredictedCenterOfMass, PredictedRotation = step.PredictedRotation,
            Number = step.Number, Substep = step.Substep, Time = step.Time,
        };

    internal JobResourceAccess[] GetInputTransferAccesses(Scheduler scheduler) =>
        [GetWriteAccess(scheduler), JobResourceAccess.Write(scheduler, _cpuVersions),
            JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
    internal IGraphicsBackend Backend => _backend;
    internal Device Device => _device;
}
