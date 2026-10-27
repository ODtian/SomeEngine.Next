using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.ECS.Systems;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace DefaultGame;

internal sealed class DeviceSceneSupportGroups : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly CommandContext? _commands;
    private readonly List<Buffer> _buffers = [];
    private readonly List<BufferUav> _views = [];
    private readonly Dictionary<string, ResourceBinding[]> _bindings = [];
    private bool _initialized;

    internal DeviceSceneSupportGroups(IGraphicsBackend backend, Device device, int bodies, int contacts, bool enabled)
    {
        _backend = backend; _device = device;
        if (!enabled) return;
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Support group buffers"));
        Add("DeviceSupportGroupRanges", bodies, 16);
        Add("DeviceSupportGroupWriteCounts", bodies, 8);
        Add("DeviceSupportGroupOrderCounts", bodies, 4);
        Add("DeviceSupportGroupBlockSums", (bodies + 255) / 256, 8);
        Add("DeviceSupportGroupBlockOffsets", (bodies + 255) / 256, 8);
        Add("DeviceSupportGroupBodies", bodies, 4);
        Add("DeviceSupportGroupContacts", contacts, 4);

        void Add(string name, int count, uint stride)
        {
            Buffer buffer = backend.CreateBuffer(device, new((ulong)count * stride,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, name), MemoryType.DeviceLocal);
            BufferUav view = backend.CreateBufferUav(device, new(buffer, new(0, buffer.Info.Size), StructureStride: stride));
            _buffers.Add(buffer); _views.Add(view);
            _bindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        }
    }

    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        foreach (var binding in _bindings) bindings.Add(binding.Key, binding.Value);
    }

    internal JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => _buffers.Select(buffer =>
        JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size)).ToArray();

    internal InitializeDeviceSceneSupportGroups Initialize() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_commands is null || _initialized) return dependency;
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. GetWriteAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, dependency);
    }

    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _backend.Begin(_commands!, new(0, 0, 16));
        if (!_initialized)
            foreach (Buffer buffer in _buffers)
                _backend.Barrier(_commands!, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands!);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    public void Dispose()
    {
        _commands?.Dispose();
        foreach (BufferUav view in _views) view.Dispose();
        foreach (Buffer buffer in _buffers) buffer.Dispose();
    }
}

internal readonly struct InitializeDeviceSceneSupportGroups(DeviceSceneSupportGroups groups) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => groups.Schedule(in context, dependency);
}
