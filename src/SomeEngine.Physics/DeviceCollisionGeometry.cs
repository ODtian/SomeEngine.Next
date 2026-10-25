using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.ECS.Systems;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Device geometry frames and parameters derived from the selected body representation.</summary>
public sealed partial class DeviceCollisionGeometry : IDisposable
{
    public const int InputStride = 112;
    private readonly IGraphicsBackend _backend;
    private readonly List<IDisposable> _objects = [];
    private MappedBuffer _mapping;
    private bool _initialized;

    public DeviceCollisionGeometry(IGraphicsBackend backend, Device device, DeviceGeometryCapacity capacity)
    {
        _backend = backend;
        Capacity = capacity;
        Inputs = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bodies * InputStride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
            "Device collision geometry"), MemoryType.DeviceLocal));
        InputView = Keep(backend.CreateBufferUav(device, new(Inputs, new(0, Inputs.Info.Size), StructureStride: InputStride)));
        Upload = Keep(backend.CreateBuffer(device, new(Inputs.Info.Size, BufferUsages.CopySource,
            "Device geometry upload"), MemoryType.Upload));
        _mapping = backend.Map(Upload, MapType.Write, BufferRange.Whole);
        CreateHullTransfers(device);
    }

    public DeviceGeometryCapacity Capacity { get; }
    public Buffer Inputs { get; }
    public Buffer Upload { get; }
    internal BufferUav InputView { get; }

    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceGeometries", [ResourceBinding.WritableBuffer(InputView)]);
        foreach (HullTransfer transfer in _hullTransfers)
            bindings.Add(transfer.Name, [ResourceBinding.WritableBuffer(transfer.View)]);
        AddReferenceBindings(bindings);
    }

    internal void WriteInput(int index, in DeviceGeometryInput input) =>
        MemoryMarshal.Write(_mapping.Bytes.Slice(index * InputStride, InputStride), in input);

    internal void CollectAccesses(DeviceInputTransfer transfer, ref JobResourceAccessBuilder resources)
    {
        if (transfer == DeviceInputTransfer.Upload)
        {
            resources.Write(Inputs, 0, (long)Inputs.Info.Size);
            resources.Read(Upload, 0, (long)Upload.Info.Size);
        }
        else resources.Read(Inputs, 0, (long)Inputs.Info.Size);
        CollectHullAccesses(transfer, ref resources);
        CollectReferenceAccesses(transfer, ref resources);
    }

    internal void UploadInputs(CommandContext commands, DeviceInputTransfer transfer)
    {
        if (!_initialized)
        {
            _backend.Barrier(commands, new BufferBarrier(Inputs, Inputs.InitialSync, PipelineSync.ComputeShading,
                Inputs.InitialAccess, ResourceAccess.UnorderedAccess));
            _initialized = true;
        }
        if (transfer == DeviceInputTransfer.Upload)
        {
            _mapping.Flush(_mapping.Range);
            _backend.Barrier(commands, new BufferBarrier(Inputs, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
            _backend.CopyBuffer(commands, new BufferCopy(Upload, 0, Inputs, 0, Inputs.Info.Size));
            _backend.Barrier(commands, new BufferBarrier(Inputs, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        UploadHullInputs(commands, transfer);
        UploadReferences(commands, transfer);
    }

    private T Keep<T>(T value) where T : IDisposable
    {
        _objects.Add(value);
        return value;
    }

    public void Dispose()
    {
        _mapping.Dispose();
        if (Bodies is not null) _referenceMapping.Dispose();
        foreach (HullTransfer transfer in _hullTransfers) transfer.Mapping.Dispose();
        for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose();
    }
}
