using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct DeviceContactConstraint
{
    [FieldOffset(0)] public uint BodyCount;
    [FieldOffset(4)] public uint FirstBodyCount;
    [FieldOffset(8)] public uint BodyOffset;
    [FieldOffset(12)] public uint WarmStart;
    [FieldOffset(16)] public Vector3 Normal;
    [FieldOffset(28)] public float Separation;
    [FieldOffset(32)] public Vector3 BodyAnchorDelta;
    [FieldOffset(44)] public float NormalStiffness;
    [FieldOffset(48)] public float NormalDamping;
    [FieldOffset(52)] public float StaticFriction;
    [FieldOffset(56)] public float DynamicFriction;
    [FieldOffset(60)] public float Restitution;
    [FieldOffset(64)] public float RestitutionThreshold;
    [FieldOffset(68)] public uint Predicted;
    [FieldOffset(72)] public double SampleTime;
}

/// <summary>Weighted anchor occurrences shared by contact solvers; the capacity is supplied by the project.</summary>
public sealed class DeviceContactBodies : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _upload;
    private readonly BufferUav _view;
    private readonly CommandContext _commands;
    private MappedBuffer _mapping;
    private bool _initialized;

    public DeviceContactBodies(IGraphicsBackend backend, Device device, int contacts, int bodiesPerContact)
    {
        _backend = backend; _device = device; BodiesPerContact = bodiesPerContact;
        Values = backend.CreateBuffer(device, new((ulong)contacts * (ulong)bodiesPerContact * 32,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
            "Contact weighted body anchors"), MemoryType.DeviceLocal);
        _view = backend.CreateBufferUav(device, new(Values, new(0, Values.Info.Size), StructureStride: 32));
        _upload = backend.CreateBuffer(device, new(Values.Info.Size, BufferUsages.CopySource, "Contact anchor inputs"), MemoryType.Upload);
        _mapping = backend.Map(_upload, MapType.Write, BufferRange.Whole);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact anchor transfer"));
    }
    public int BodiesPerContact { get; }
    public Buffer Values { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add("DeviceContactBodyLinks", [ResourceBinding.WritableBuffer(_view)]);
    public JobResourceAccess GetReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size);
    public JobResourceAccess GetUploadAccess(Scheduler scheduler) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size);
    public UploadDeviceContactBodies UploadInputs() => new(this);

    internal int Write(int rowIndex, ReadOnlySpan<ContactBodyLink> links, scoped QueryCursor access)
    {
        int offset = rowIndex * BodiesPerContact;
        Span<DeviceContactBody> values = MemoryMarshal.Cast<byte, DeviceContactBody>(_mapping.Bytes);
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            access.TryGetRow(link.Participant.Entity, out QueryRow participant);
            var body = participant.Read<DirectedRelationEndpoints<ContactParticipant>>().Target;
            access.TryGetRow(body, out QueryRow bodyRow);
            values[offset + index] = new(new(body, link.LocalPoint, link.Weight), (uint)bodyRow.Read<DeviceBodyBinding>().Index);
        }
        return offset;
    }
    internal JobHandle Schedule(Scheduler scheduler, JobHandle dependency) => DevicePhysicsJobs.Schedule(scheduler, this,
        [JobResourceAccess.Read(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))], dependency);
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _mapping.Flush(_mapping.Range);
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(Values, _initialized ? PipelineSync.ComputeShading : Values.InitialSync,
            PipelineSync.Copy, _initialized ? ResourceAccess.UnorderedAccess : Values.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(_upload, 0, Values, 0, Values.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(Values, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    public void Dispose() { _mapping.Dispose(); _commands.Dispose(); _view.Dispose(); _upload.Dispose(); Values.Dispose(); }
}
public readonly struct UploadDeviceContactBodies(DeviceContactBodies values) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        values.Schedule(WorldJobs.GetScheduler(context.World), dependency);
}

public readonly struct DeviceContactSource(DeviceContactBodies bodies) : IDeviceConstraintSource<ContactPoint, DeviceContactConstraint>
{
    public static void Collect(in ContactPoint component, ref JobResourceAccessBuilder resources) { }
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceBodyBinding>()
        .WithOptionalRead<DeviceConstraintBinding<DeviceContactConstraint>>().WithAccess(new QueryDefinitionBuilder()
            .Read<ContactMaterial>().ReadBuffer<ContactBodyLink>().Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).Build());
    public DeviceContactConstraint Read(in ContactPoint component, scoped QueryRow row, scoped QueryCursor access)
    {
        ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
        int offset = bodies.Write(row.Read<DeviceConstraintBinding<DeviceContactConstraint>>().Index, links, access);
        ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
        return new()
        {
            BodyCount = (uint)links.Length, FirstBodyCount = (uint)component.FirstBodyCount, BodyOffset = (uint)offset,
            WarmStart = component.WarmStart ? 1u : 0u, Normal = component.Normal, Separation = component.Separation,
            BodyAnchorDelta = component.BodyAnchorDelta, NormalStiffness = material.NormalStiffness, NormalDamping = material.NormalDamping,
            StaticFriction = material.StaticFriction, DynamicFriction = material.DynamicFriction,
            Restitution = material.Restitution, RestitutionThreshold = material.RestitutionThreshold,
            Predicted = component.Prediction.Enabled ? 1u : 0u, SampleTime = component.Prediction.SampleTime,
        };
    }
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceAvbdContactState
{
    [FieldOffset(0)] public float NormalMultiplier;
    [FieldOffset(4)] public float NormalPenalty;
    [FieldOffset(8)] public float TangentPenalty;
    [FieldOffset(12)] public float FrictionRegularizationSpeed;
    [FieldOffset(16)] public Vector3 MultiplierNormal;
    [FieldOffset(28)] public float InitialSeparation;
    [FieldOffset(32)] public Vector3 InitialDelta;
    [FieldOffset(48)] public Vector3 TangentMultiplier;
}
public readonly struct DeviceAvbdContactStateSource : IDeviceAvbdStateSource<AvbdContactState, DeviceAvbdContactState>
{
    public DeviceAvbdContactState Read(in AvbdContactState component) => new()
    {
        NormalMultiplier = component.NormalMultiplier, NormalPenalty = component.NormalPenalty,
        TangentPenalty = component.TangentPenalty, FrictionRegularizationSpeed = component.FrictionRegularizationSpeed,
        MultiplierNormal = component.MultiplierNormal, InitialSeparation = component.InitialSeparation,
        InitialDelta = component.InitialDelta, TangentMultiplier = component.TangentMultiplier,
    };
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct DeviceContactResponseState
{
    [FieldOffset(0)] public float InitialNormalVelocity;
    [FieldOffset(4)] public float RestitutionImpulse;
    [FieldOffset(16)] public Vector3 ForceOnSecond;
    [FieldOffset(32)] public double StartTime;
    [FieldOffset(40)] public double EndTime;
}
public readonly record struct DeviceContactResponseBinding(DeviceConstraintState<DeviceContactResponseState> Responses) : IComponent;
public readonly struct DeviceContactResponseResources : IJobComponentResources<DeviceContactResponseBinding>
{
    public static void Collect(in DeviceContactResponseBinding component, ref JobResourceAccessBuilder resources) { }
}
public readonly struct DeviceContactResponseRows(DeviceConstraintState<DeviceContactResponseState> responses) : IDeviceConstraintRowSelection
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceContactResponseBinding>();
    public void AddReadResources(QueryReadResources resources) => resources.Add<DeviceContactResponseBinding, DeviceContactResponseResources>();
    public bool Matches(scoped QueryRow row) => row.Has<ContactResponse>() && row.TryRead<DeviceContactResponseBinding>(out var binding)
        && ReferenceEquals(binding.Responses, responses);
}
