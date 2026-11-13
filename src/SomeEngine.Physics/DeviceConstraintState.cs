using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Chooses the constraint's XPBD history buffer; its parameter binding supplies the row index.</summary>
public readonly record struct DeviceXpbdState<TState>(DeviceConstraintState<TState> States) : ITrackedComponent where TState : unmanaged;

public readonly struct DeviceXpbdStateResources<TState> : IJobComponentResources<DeviceXpbdState<TState>> where TState : unmanaged
{
    public static void Collect(in DeviceXpbdState<TState> component, ref JobResourceAccessBuilder resources) { }
}

public readonly struct DeviceXpbdStateRows<TState>(DeviceConstraintState<TState> states) : IDeviceConstraintRowSelection where TState : unmanaged
{
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.ReadOptional<DeviceXpbdState<TState>>(true)];
    public Query AddReads(Query query) => query.WithOptionalRead<DeviceXpbdState<TState>>();
    public bool Matches(QueryChunk chunk, int row) => chunk.Has<DeviceXpbdState<TState>>(row)
        && ReferenceEquals(chunk.Read<DeviceXpbdState<TState>>()[row].States, states);
    public bool HasChangedInputs(QueryChunk chunk, ulong since) => chunk.WasWritten<DeviceXpbdState<TState>>(since);
}

/// <summary>Per-constraint numerical history in the selected device representation.</summary>
public sealed class DeviceConstraintState<TState> : IDisposable where TState : unmanaged
{
    private readonly string _bindingName;
    private bool _initialized;
    internal readonly BufferUav View;
    internal readonly DeviceInputPages Inputs;
    public DeviceConstraintState(IGraphicsBackend backend, Device device, int capacity, string bindingName, DeviceContactMemory? memory = null)
    {
        Inputs = new(backend, device, capacity, Unsafe.SizeOf<TState>(), bindingName + " inputs");
        _bindingName = bindingName; Capacity = capacity; Memory = memory;
        uint stride = (uint)Unsafe.SizeOf<TState>();
        var description = new BufferDesc((ulong)capacity * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination, bindingName);
        States = memory is null ? backend.CreateBuffer(device, description) : memory.CreateBuffer(description, stride);
        View = backend.CreateBufferUav(device, new(States, new(0, States.Info.Size), StructureStride: stride));
    }
    public int Capacity { get; }
    public DeviceContactMemory? Memory { get; }
    public Buffer States { get; }
    internal void BeginWrite(IGraphicsBackend backend, CommandContext commands, PipelineSync sync, ResourceAccess access)
    {
        bool continuing = _initialized || Memory?.UsesSparseBuffers == true;
        backend.Barrier(commands, new BufferBarrier(States, continuing ? PipelineSync.ComputeShading : States.InitialSync,
            sync, continuing ? ResourceAccess.UnorderedAccess : States.InitialAccess, access));
        _initialized = true;
    }
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add(_bindingName, [ResourceBinding.WritableBuffer(View)]);
    public JobResourceAccess GetWriteAccess(Scheduler scheduler) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);
    public JobResourceAccess GetReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);
    public void Dispose() { Inputs.Dispose(); View.Dispose(); States.Dispose(); }
}
