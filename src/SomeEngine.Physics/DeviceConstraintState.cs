using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Chooses the constraint's XPBD history buffer; its parameter binding supplies the row index.</summary>
public readonly record struct DeviceXpbdState<TState>(DeviceConstraintState<TState> States) : IComponent where TState : unmanaged;

public readonly struct DeviceXpbdStateResources<TState> : IJobComponentResources<DeviceXpbdState<TState>> where TState : unmanaged
{
    public static void Collect(in DeviceXpbdState<TState> component, ref JobResourceAccessBuilder resources) { }
}

public readonly struct DeviceXpbdStateRows<TState>(DeviceConstraintState<TState> states) : IDeviceConstraintRowSelection where TState : unmanaged
{
    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<DeviceXpbdState<TState>>();
    public void AddReadResources(QueryReadResources resources) => resources.Add<DeviceXpbdState<TState>, DeviceXpbdStateResources<TState>>();
    public bool Matches(scoped QueryRow row) =>
        row.TryRead<DeviceXpbdState<TState>>(out var binding) && ReferenceEquals(binding.States, states);
}

/// <summary>Per-constraint numerical history in the selected device representation.</summary>
public sealed class DeviceConstraintState<TState> : IDisposable where TState : unmanaged
{
    private readonly string _bindingName;
    internal readonly BufferUav View;
    public DeviceConstraintState(IGraphicsBackend backend, Device device, int capacity, string bindingName)
    {
        _bindingName = bindingName; Capacity = capacity;
        uint stride = (uint)Unsafe.SizeOf<TState>();
        States = backend.CreateBuffer(device, new((ulong)capacity * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            bindingName), MemoryType.DeviceLocal);
        View = backend.CreateBufferUav(device, new(States, new(0, States.Info.Size), StructureStride: stride));
    }
    public int Capacity { get; }
    public Buffer States { get; }
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add(_bindingName, [ResourceBinding.WritableBuffer(View)]);
    public JobResourceAccess GetWriteAccess(Scheduler scheduler) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);
    public JobResourceAccess GetReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);
    public void Dispose() { View.Dispose(); States.Dispose(); }
}
