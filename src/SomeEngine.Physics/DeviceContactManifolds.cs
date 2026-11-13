using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Current and preceding device contact columns and the selected solver's history.</summary>
public abstract class DeviceContactManifolds : IDisposable
{
    private readonly Buffer _zeros;
    internal readonly Buffer Defaults;
    internal readonly BufferSrv DefaultWords;
    internal readonly Buffer ResponseDefaults;
    internal readonly BufferSrv ResponseDefaultWords;
    private bool _initialized;
    internal readonly DeviceContactColumn DispatchValues;
    internal readonly DeviceContactColumn MatchValues;
    internal readonly DeviceContactColumn RemovedValues;
    public Buffer RemovedPoints => RemovedValues.Buffer;
    public ulong MatchingNumber { get; private set; }
    public Buffer PreviousPoints => MatchValues.Buffer;
    internal readonly IndirectCommandLayout DispatchLayout;

    internal DeviceContactManifolds(IGraphicsBackend backend, Device device, DeviceContactCapacity capacity,
        ReadOnlySpan<byte> defaults)
    {
        Capacity = capacity;
        DispatchValues = new(backend, device, "DispatchArguments", 9, 4, BufferUsages.Indirect);
        MatchValues = new(backend, device, "PreviousPoints", capacity.Points, 8);
        RemovedValues = new(backend, device, "RemovedPoints", capacity.Points + 1, 4);
        DispatchLayout = backend.CreateIndirectCommandLayout(device, new([new(IndirectArgumentType.Dispatch)], 12));
        Current = new(backend, device, capacity, (uint)defaults.Length);
        Previous = new(backend, device, capacity, (uint)defaults.Length);
        Defaults = backend.CreateBuffer(device, new((ulong)defaults.Length, BufferUsages.ShaderRead,
            "Contact state defaults"), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(Defaults, MapType.Write, BufferRange.Whole))
        {
            defaults.CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        DefaultWords = backend.CreateBufferSrv(device, new(Defaults, BufferRange.Whole, StructureStride: 4));
        ResponseDefaults = backend.CreateBuffer(device, new(64, BufferUsages.ShaderRead, "Contact response defaults"), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(ResponseDefaults, MapType.Write, BufferRange.Whole))
        { mapping.Bytes.Clear(); mapping.Flush(mapping.Range); }
        ResponseDefaultWords = backend.CreateBufferSrv(device, new(ResponseDefaults, BufferRange.Whole, StructureStride: 4));
        _zeros = backend.CreateBuffer(device, new(12, BufferUsages.CopySource, "Empty contact counts"), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(_zeros, MapType.Write, BufferRange.Whole))
        { mapping.Bytes.Clear(); mapping.Flush(mapping.Range); }
    }

    public DeviceContactCapacity Capacity { get; }
    public DeviceContactColumns Current { get; private set; }
    public DeviceContactColumns Previous { get; private set; }

    internal JobResourceAccess[] GetInitializationAccesses(Scheduler scheduler) => _initialized ? [] :
        [JobResourceAccess.Write(scheduler.GetResourceToken(Current.Counts),0,12),
         JobResourceAccess.Write(scheduler.GetResourceToken(Previous.Counts),0,12)];

    internal void Advance()
    { (Current, Previous) = (Previous, Current); MatchingNumber++; }
    public void AddPreviousPointsBinding(Dictionary<string, ResourceBinding[]> bindings, string name = "DeviceContactPreviousPoints") =>
        bindings[name] = [ResourceBinding.ReadOnlyBuffer(MatchValues.ReadView)];
    public JobResourceAccess GetPreviousPointsReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(PreviousPoints), 0, (long)PreviousPoints.Info.Size);

    public void AddRemovedPointsBinding(Dictionary<string, ResourceBinding[]> bindings, string name = "DeviceContactRemovedPoints") =>
        bindings[name] = [ResourceBinding.ReadOnlyBuffer(RemovedValues.ReadView)];
    public JobResourceAccess GetRemovedPointsReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(RemovedPoints), 0, (long)RemovedPoints.Info.Size);

    internal void Initialize(IGraphicsBackend backend, CommandContext commands)
    {
        if (_initialized) return;
        foreach (DeviceContactColumns columns in new[] { Current, Previous })
        {
            columns.CountValues.Transition(backend, commands, PipelineSync.Copy, ResourceAccess.CopyDestination);
            backend.CopyBuffer(commands, new(_zeros, 0, columns.Counts, 0, 12));
        }
        _initialized = true;
    }

    internal void DispatchPoints(IGraphicsBackend backend, CommandContext commands)
    {
        DispatchValues.Transition(backend, commands, PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        backend.ExecuteIndirect(commands, DispatchLayout, new(DispatchValues.Buffer, new(12, 12)), 1);
        DispatchValues.Transition(backend, commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
    }

    public void Dispose()
    {
        Current.Dispose(); Previous.Dispose(); DispatchLayout.Dispose(); DispatchValues.Dispose(); MatchValues.Dispose(); RemovedValues.Dispose(); DefaultWords.Dispose(); Defaults.Dispose();
        ResponseDefaultWords.Dispose(); ResponseDefaults.Dispose(); _zeros.Dispose();
    }
}

/// <summary>The selected solver state stored beside the common device contact columns.</summary>
public sealed class DeviceContactManifolds<TState> : DeviceContactManifolds where TState : unmanaged
{
    public DeviceContactManifolds(IGraphicsBackend backend, Device device, DeviceContactCapacity capacity, in TState defaults)
        : base(backend, device, capacity, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in defaults, 1))) { }
}
