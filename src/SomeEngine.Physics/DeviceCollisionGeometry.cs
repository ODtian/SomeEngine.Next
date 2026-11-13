using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.ECS.Systems;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Device geometry frames and parameters derived from the selected body representation.</summary>
public sealed partial class DeviceCollisionGeometry : IDisposable
{
    public const int InputStride = 128;
    private readonly IGraphicsBackend _backend;
    private readonly List<IDisposable> _objects = [];
    private MappedBuffer _mapping;
    private bool _initialized;
    private bool _materialsInitialized;
    private MappedBuffer _materialMapping;
    private readonly Buffer _materialUpload;
    public Buffer Materials { get; }
    internal BufferSrv MaterialView { get; }

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
        Materials = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bodies * 32, BufferUsages.ShaderRead | BufferUsages.CopyDestination, "Geometry contact materials")));
        MaterialView = Keep(backend.CreateBufferSrv(device, new(Materials, BufferRange.Whole, StructureStride: 32)));
        _materialUpload = Keep(backend.CreateBuffer(device, new(Materials.Info.Size, BufferUsages.CopySource), MemoryType.Upload));
        _materialMapping = backend.Map(_materialUpload, MapType.Write, BufferRange.Whole);
        _materialMapping.Bytes.Clear();
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
        resources.Read(Materials, 0, (long)Materials.Info.Size);
        if (transfer == DeviceInputTransfer.Upload)
        { resources.Write(Materials, 0, (long)Materials.Info.Size); resources.Read(_materialUpload, 0, (long)_materialUpload.Info.Size); }
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
        UploadMaterials(commands, transfer);
        UploadHullInputs(commands, transfer);
        UploadReferences(commands, transfer);
    }

    internal void WriteMaterial(int index, in ContactMaterial value)
    {
        Span<byte> stored = _materialMapping.Bytes.Slice(index * 32, 32);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
        if (MemoryMarshal.Read<uint>(stored[24..]) != 0 && bytes.SequenceEqual(stored[..24])) return;
        uint revision = MemoryMarshal.Read<uint>(stored[28..]) + 1;
        var input = new MaterialInput(value, 1, revision);
        MemoryMarshal.Write(stored, in input);
    }
    internal void CollectMaterialUpload(int index, ref JobResourceAccessBuilder resources) => resources.Write(_materialUpload, (long)index * 32, 32);
    private void UploadMaterials(CommandContext commands, DeviceInputTransfer transfer)
    {
        if (!_materialsInitialized)
        {
            _backend.Barrier(commands, new BufferBarrier(Materials, Materials.InitialSync, PipelineSync.ComputeShading,
                Materials.InitialAccess, ResourceAccess.ShaderResource)); _materialsInitialized = true;
        }
        if (transfer != DeviceInputTransfer.Upload) return;
        _materialMapping.Flush(_materialMapping.Range);
        _backend.Barrier(commands, new BufferBarrier(Materials, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.ShaderResource, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(commands, new(_materialUpload, 0, Materials, 0, Materials.Info.Size));
        _backend.Barrier(commands, new BufferBarrier(Materials, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
    }
    [StructLayout(LayoutKind.Sequential, Size = 32)] private readonly record struct MaterialInput(ContactMaterial Material, uint Present, uint Revision);

    private T Keep<T>(T value) where T : IDisposable
    {
        _objects.Add(value);
        return value;
    }

    public void Dispose()
    {
        _mapping.Dispose(); _materialMapping.Dispose();
        if (Bodies is not null) _referenceMapping.Dispose();
        foreach (HullTransfer transfer in _hullTransfers) transfer.Mapping.Dispose();
        for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose();
    }
}
