using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The geometry entities joined by one contact whose numerical data resides in its bound resource.</summary>
public readonly record struct ContactPointReference(Entity First, Entity Second) : IComponent;

public interface IContactIdentityComponents
{
    void AddReads(World world, List<JobResourceAccess> accesses) { }
    void Add(ref JobCommandWriter commands, DeferredEntity point, in ContactPointReference geometry,
        in PhysicsStepContext context);
}

/// <summary>Maintains actual contact entities from device lifecycle changes and transfers explicitly authored inputs.</summary>
public sealed partial class ContactIdentities<TCombine, TComponents> : IDisposable
    where TCombine : struct, IContactMaterialCombine
    where TComponents : struct, IContactIdentityComponents
{
    private readonly DeviceContactMatches _matches;
    private readonly DeviceConstraintData<DeviceContactConstraint> _parameters;
    private readonly World _world;
    private readonly QueryHandle _points, _materials;
    private readonly JobResourceAccess[] _pointAccesses, _materialAccesses;
    private readonly TCombine _combine;
    private readonly TComponents _components;
    private CreatedContact[] _created = [];
    private ContactInputVersions[] _inputVersions = [];
    private uint _inputVersion;
    private (uint References, uint Bindings, uint Materials, uint Triggers) _sourceVersions;

    public ContactIdentities(World world, QueryHandle points, DeviceContactMatches matches,
        DeviceConstraintData<DeviceContactConstraint> parameters, ContactIdentityKernels kernels, TCombine combine, TComponents components)
    {
        _world = world; _matches = matches; _parameters = parameters; _combine = combine; _components = components;
        _backend = ((IDevicePhysicsSubmission)parameters).Backend; _device = ((IDevicePhysicsSubmission)parameters).Device;
        _inputs = _backend.CreateBuffer(_device, new(InputStride, BufferUsages.ShaderRead, "Contact identity input changes"), MemoryType.Upload);
        _inputMapping = _backend.Map(_inputs, MapType.Write, BufferRange.Whole);
        _inputView = _backend.CreateBufferSrv(_device, new(_inputs, new(0, _inputs.Info.Size), StructureStride: InputStride));
        _counts = _backend.CreateBuffer(_device, new(256, BufferUsages.Constant), MemoryType.Upload);
        _countsView = _backend.CreateBufferCbv(_device, new(_counts, new(0, 256)));
        _commands = _backend.CreateCommandContext(_device, new(QueueType.Compute, 0, 1, Label: "Contact identity inputs"));
        ComputePipelineDesc[] descriptions = [kernels.Remove, kernels.Write];
        _pipelines = descriptions.Select(kernel => _backend.CreateComputePipeline(_device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? SlangShaderSharp.VariableLayoutReflection.Null).ToArray();
        QueryDefinition reads = world.GetQueryDefinition(points).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<ContactPointReference>().Read<DeviceConstraintBinding<DeviceContactConstraint>>()
            .Optional<TriggerContact>(QueryAccess.Read).Build());
        _points = world.Query(reads);
        QueryDefinition materials = new QueryDefinitionBuilder().Read<ContactMaterial>().Build();
        _materials = world.Query(materials);
        _pointAccesses = new JobResourceAccess[QueryJobAccess.Count(reads)];
        _materialAccesses = new JobResourceAccess[QueryJobAccess.Count(materials)];
        var resources = new QueryReadResources();
        resources.Add<DeviceConstraintBinding<DeviceContactConstraint>, DeviceConstraintReferenceResources<DeviceContactConstraint>>();
        QueryJobAccess.CopyAccesses(world, reads, _pointAccesses, resources);
        QueryJobAccess.CopyAccesses(world, materials, _materialAccesses);
        matches.ReadIdentityReferences();
    }

    public UploadContactIdentityInputs<TCombine, TComponents> UploadInputs() => new(this);
    public UpdateContactIdentities<TCombine, TComponents> Update() => new(this);

    internal JobHandle ScheduleInputs(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobResourceAccess[] accesses = [.. _pointAccesses, .. _materialAccesses, _matches.WriteAccess,
            _parameters.GetRowAllocationAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(this))];
        return scheduler.Schedule(new RefreshInputs(this, context), accesses, dependency);
    }

    internal JobHandle ScheduleUpdate(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        return scheduler.Schedule(new UpdateContacts(this, context),
            [_matches.WriteAccess, JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
    }

    private JobHandle ScheduleChanges(in PhysicsStepContext context)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        var commands = new JobCommandBuffer(_world, 1);
        var accesses = new List<JobResourceAccess>(_pointAccesses) { _matches.WriteAccess,
            _parameters.GetRowAllocationAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        _components.AddReads(_world, accesses);
        commands.Schedule(0, new CreateContacts(this, context), CollectionsMarshal.AsSpan(accesses));
        JobHandle playback = commands.SchedulePlayback();
        JobResourceAccess[] bindingAccesses = [.. _pointAccesses, .. _materialAccesses, _matches.WriteAccess,
            _parameters.GetRowAllocationAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(this))];
        JobHandle bound = scheduler.Schedule(new BindContacts(this, context), bindingAccesses, playback);
        JobHandle released = scheduler.ScheduleFinally(new ReleaseCommands(commands), JobScheduleOptions.Default, bound);
        return scheduler.CombineDependencies([bound, released]);
    }

    private void Create(in PhysicsStepContext context, ref JobCommandWriter commands)
    {
        foreach (Entity point in _matches.RemovedPoints)
            if (_world.IsAlive(point)) commands.DestroyEntity(point);
        int count = _matches.NewInputCount;
        _created = new CreatedContact[count];
        if (count == 0) return;
        var allocatedRows = new int[count];
        _matches.ReserveIdentityRows(allocatedRows);
        using MappedBuffer mapping = _matches.MapNewReferences();
        ReadOnlySpan<ContactPointReference> references = MemoryMarshal.Cast<byte, ContactPointReference>(mapping.Bytes);
        for (int input = 0; input < count; input++)
        {
            int row = allocatedRows[input];
            _matches.RecordIdentityRow(input, row);
            DeferredEntity point = commands.CreateEntity();
            using var additions = commands.AddComponents(point);
            commands.Add(point, references[input]);
            commands.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(_parameters, row));
            _components.Add(ref commands, point, in references[input], in context);
            _created[input] = new(point, row);
        }
    }

    private void Bind(in PhysicsStepContext context)
    {
        BeginInputs(_created.Length); _removedCount = _matches.RemovedParameterRows.Length; _inputVersion++;
        if (_created.Length != 0)
        {
            var bind = new BindRows(this);
            _world.ExecuteReadQuery(_materials, ref bind, static (scoped QueryCursor rows, ref BindRows bind) => bind.Execute(rows));
        }
        _created = [];
        _sourceVersions = ReadSourceVersions();
        SubmitInputs();
    }

    private readonly struct BindRows(ContactIdentities<TCombine, TComponents> contacts)
    {
        internal void Execute(scoped QueryCursor materials)
        {
            var bind = new BindCreatedRows(contacts, materials);
            contacts._world.ExecuteReadQuery(contacts._points, ref bind,
                static (scoped QueryCursor rows, ref BindCreatedRows state) => state.Execute(rows));
        }
    }

    private readonly ref struct BindCreatedRows(ContactIdentities<TCombine, TComponents> contacts, QueryCursor materials)
    {
        private readonly QueryCursor _materials = materials;
        internal void Execute(scoped QueryCursor points)
        {
            for (int input = 0; input < contacts._created.Length; input++)
            {
                CreatedContact created = contacts._created[input];
                Entity point = created.Point.Resolve();
                points.TryGetRow(point, out QueryRow row);
                contacts.WriteInputs(created.Row, row, _materials, contacts._matches.NewInputIndices[input]);
            }
        }
    }

    private void Refresh(in PhysicsStepContext context)
    {
        var versions = ReadSourceVersions();
        BeginInputs(0);
        if (versions != _sourceVersions)
        {
            _inputVersion++;
            var read = new ReadMaterialInputs(this);
            _world.ExecuteReadQuery(_materials, ref read, static (scoped QueryCursor rows, ref ReadMaterialInputs state) => state.Execute(rows));
            _sourceVersions = versions;
        }
        SubmitInputs();
    }

    private readonly struct ReadMaterialInputs(ContactIdentities<TCombine, TComponents> contacts)
    {
        internal void Execute(scoped QueryCursor materials)
        {
            var read = new ReadPointInputs(contacts, materials);
            contacts._world.ExecuteReadQuery(contacts._points, ref read,
                static (scoped QueryCursor rows, ref ReadPointInputs state) => state.Execute(rows));
        }
    }

    private readonly ref struct ReadPointInputs(ContactIdentities<TCombine, TComponents> contacts, QueryCursor materials)
    {
        private readonly QueryCursor _materials = materials;
        internal void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                var binding = row.Read<DeviceConstraintBinding<DeviceContactConstraint>>();
                if (!ReferenceEquals(binding.Data, contacts._parameters)) continue;
                contacts.WriteInputs(binding.Index, row, _materials);
            }
        }
    }

    private void WriteInputs(int row, scoped QueryRow point, scoped QueryCursor materials, int detectedInput = -1)
    {
        ref readonly ContactPointReference reference = ref point.ReadRef<ContactPointReference>();
        if (!materials.TryGetRow(reference.First, out QueryRow first)
            || !materials.TryGetRow(reference.Second, out QueryRow second)) return;
        if (row >= _inputVersions.Length)
            Array.Resize(ref _inputVersions, Math.Max(row + 1, Math.Max(16, _inputVersions.Length * 3 / 2)));
        var version = new ContactInputVersions(point.WriteVersion<ContactPointReference>(),
            point.WriteVersion<DeviceConstraintBinding<DeviceContactConstraint>>(), first.WriteVersion<ContactMaterial>(),
            second.WriteVersion<ContactMaterial>(), point.Has<TriggerContact>());
        if (_matches.HasContactIdentity(row, point.Entity) && _inputVersions[row] == version) return;
        _matches.StoreContactIdentity(row, point.Entity);
        ref readonly ContactMaterial firstMaterial = ref first.ReadRef<ContactMaterial>();
        ref readonly ContactMaterial secondMaterial = ref second.ReadRef<ContactMaterial>();
        ContactMaterial material = _combine.Combine(in firstMaterial, in secondMaterial);
        uint warmStart = _matches.WarmStart ? 1u : 0u, triggered = version.Trigger ? 1u : 0u;
        AddInputChange(row, point.Entity, warmStart, triggered, in material, detectedInput);
        _inputVersions[row] = version;
    }

    private (uint References, uint Bindings, uint Materials, uint Triggers) ReadSourceVersions() =>
        (_world.ComponentWriteVersion<ContactPointReference>(), _world.ComponentWriteVersion<DeviceConstraintBinding<DeviceContactConstraint>>(),
            _world.ComponentWriteVersion<ContactMaterial>(), _world.ComponentWriteVersion<TriggerContact>());

    private readonly struct Transfer(ContactIdentities<TCombine, TComponents> contacts) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => ((IDevicePhysicsSubmission)contacts._parameters).Backend;
        public Device Device => ((IDevicePhysicsSubmission)contacts._parameters).Device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => contacts.SubmitChanges();
    }

    private readonly struct RefreshInputs(ContactIdentities<TCombine, TComponents> contacts, PhysicsStepContext context) : IJob
    { public void Execute() => contacts.Refresh(in context); }
    private readonly struct UpdateContacts(ContactIdentities<TCombine, TComponents> contacts, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            if (contacts._matches.NewInputCount != 0 || !contacts._matches.RemovedParameterRows.IsEmpty)
                contacts.ScheduleChanges(in context);
        }
    }
    private readonly struct CreateContacts(ContactIdentities<TCombine, TComponents> contacts, PhysicsStepContext context) : IJobCommandProducer
    { public void Execute(ref JobCommandWriter commands) => contacts.Create(in context, ref commands); }
    private readonly struct BindContacts(ContactIdentities<TCombine, TComponents> contacts, PhysicsStepContext context) : IJob
    { public void Execute() => contacts.Bind(in context); }
    private readonly struct ReleaseCommands(JobCommandBuffer commands) : IJob
    { public void Execute() => commands.Dispose(); }
    private readonly record struct CreatedContact(DeferredEntity Point, int Row);
    private readonly record struct ContactInputVersions(uint Reference, uint Binding, uint FirstMaterial, uint SecondMaterial, bool Trigger);
    public void Dispose()
    {
        _world.ReleaseQuery(_points); _world.ReleaseQuery(_materials);
        _inputMapping.Dispose(); _inputView.Dispose(); _inputs.Dispose(); _countsView.Dispose(); _counts.Dispose();
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose();
    }
}

public readonly struct UploadContactIdentityInputs<TCombine, TComponents>(ContactIdentities<TCombine, TComponents> contacts) : IPhysicsStage
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactIdentityComponents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => contacts.ScheduleInputs(in context, dependency);
}

public readonly struct UpdateContactIdentities<TCombine, TComponents>(ContactIdentities<TCombine, TComponents> contacts) : IPhysicsStage
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactIdentityComponents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => contacts.ScheduleUpdate(in context, dependency);
}
