using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodies
{
    private readonly BodyInputVersions[] _cpuVersions;
    [Flags]
    internal enum BodyInputComponents : byte { Pose = 1, Mass = 2, Velocity = 4, Step = 8, All = 15, NewEntity = 16 }
    private readonly BodyInputComponents[] _inputComponents;
    internal readonly record struct BodyInputVersions(Entity Entity, ulong Pose, ulong Mass, ulong Velocity, ulong Step,
        BodyInputComponents Known, uint InputVersion = 0);

    internal BodyInputVersions ReadCpuVersions(int index) => _cpuVersions[index];
    internal void AcceptCpuVersions(int index, in BodyInputVersions version) => _cpuVersions[index] = version;
    internal void AcceptCpuResult(int index, Entity entity, ulong version, BodyInputComponents components)
    {
        BodyInputVersions previous = _cpuVersions[index];
        _cpuVersions[index] = previous with
        {
            Entity = entity,
            Pose = (components & BodyInputComponents.Pose) != 0 ? version : previous.Pose,
            Velocity = (components & BodyInputComponents.Velocity) != 0 ? version : previous.Velocity,
            Step = (components & BodyInputComponents.Step) != 0 ? version : previous.Step,
            Known = previous.Known | components,
        };
    }
    internal void CollectCpuResult(int index, ref JobResourceAccessBuilder resources) =>
        CollectCpuResults(index, 1, ref resources);
    internal void CollectCpuResults(int first, int count, ref JobResourceAccessBuilder resources) =>
        resources.Write(_cpuVersions.AsMemory(first, count));
    internal Span<DeviceBodyState> InputStates(int first, int count) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, DeviceBodyState>(_mapping.Bytes).Slice(first, count);
    internal void SetInputComponents(int index, BodyInputComponents components) => _inputComponents[index] = components;
    internal BodyInputComponents TakeInputComponents(int index)
    {
        BodyInputComponents components = _inputComponents[index];
        _inputComponents[index] = 0;
        return components;
    }
    internal void CollectInputWrites(int first, int count, ref JobResourceAccessBuilder resources)
    {
        resources.Write(_cpuVersions.AsMemory(first, count));
        resources.Write(_inputComponents.AsMemory(first, count));
        resources.Write(_upload, (long)first * StateStride, (long)count * StateStride);
    }
    internal void CollectInputComponents(int first, int count, ref JobResourceAccessBuilder resources) =>
        resources.Write(_inputComponents.AsMemory(first, count));

    internal static DeviceBodyState ReadInput(Entity entity, in BodyPose pose, in BodyMass mass,
        in BodyVelocity velocity, in BodyStepState step) => new()
        {
            Position = pose.Position, Rotation = pose.Rotation, Motion = (uint)mass.Motion,
            LinearVelocity = velocity.Linear, AngularVelocity = velocity.Angular, InverseMass = mass.InverseMass,
            CenterOfMass = mass.CenterOfMass, EntityIndex = (uint)entity.Id, EntityGeneration = (uint)entity.Generation,
            InverseInertiaX = new(mass.InverseInertia.M11, mass.InverseInertia.M12, mass.InverseInertia.M13),
            InverseInertiaY = new(mass.InverseInertia.M21, mass.InverseInertia.M22, mass.InverseInertia.M23),
            InverseInertiaZ = new(mass.InverseInertia.M31, mass.InverseInertia.M32, mass.InverseInertia.M33),
            StartCenterOfMass = step.StartCenterOfMass, StartRotation = step.StartRotation,
            PredictedCenterOfMass = step.PredictedCenterOfMass, PredictedRotation = step.PredictedRotation,
            Number = step.Number, Substep = step.Substep, Time = step.Time,
        };

    internal JobResourceAccess[] GetInputTransferAccesses(Scheduler scheduler) =>
        [GetWriteAccess(scheduler), JobResourceAccess.Read(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands)), .. GetCpuInputTransferAccesses(scheduler)];
    internal IGraphicsBackend Backend => _backend;
    internal Device Device => _device;
}
