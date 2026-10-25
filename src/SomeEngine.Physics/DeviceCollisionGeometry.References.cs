using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGeometry
{
    private Buffer? _referenceUpload;
    private BufferUav? _referenceView, _pointView;
    private MappedBuffer _referenceMapping;
    private bool _referencesInitialized;

    public DeviceCollisionGeometry(IGraphicsBackend backend, Device device, DeviceGeometryCapacity capacity, DeviceBodies bodies)
        : this(backend, device, capacity)
    {
        Bodies = bodies;
        References = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bodies * 64,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Geometry body references"), MemoryType.DeviceLocal));
        _referenceView = Keep(backend.CreateBufferUav(device, new(References, new(0, References.Info.Size), StructureStride: 64)));
        Points = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bodies * 3 * 12,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Current geometry points"), MemoryType.DeviceLocal));
        _pointView = Keep(backend.CreateBufferUav(device, new(Points, new(0, Points.Info.Size), StructureStride: 12)));
        _referenceUpload = Keep(backend.CreateBuffer(device, new(References.Info.Size, BufferUsages.CopySource,
            "Geometry reference upload"), MemoryType.Upload));
        _referenceMapping = backend.Map(_referenceUpload, MapType.Write, BufferRange.Whole);
        _referenceMapping.Bytes.Clear();
    }

    public DeviceBodies? Bodies { get; }
    public Buffer? References { get; }
    public Buffer? Points { get; }

    internal void WriteReference(int index, in DeviceGeometryReference value) =>
        MemoryMarshal.Write(_referenceMapping.Bytes.Slice(index * 64, 64), in value);
    internal void CollectReferenceUpload(int index, ref JobResourceAccessBuilder resources) =>
        resources.Write(_referenceUpload!, (long)index * 64, 64);

    private void AddReferenceBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        if (Bodies is null) return;
        bindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(Bodies.View)]);
        bindings.Add("DeviceGeometryReferences", [ResourceBinding.WritableBuffer(_referenceView!)]);
        bindings.Add("DeviceGeometryPoints", [ResourceBinding.WritableBuffer(_pointView!)]);
    }

    private void CollectReferenceAccesses(DeviceInputTransfer transfer, ref JobResourceAccessBuilder resources)
    {
        if (Bodies is null) return;
        resources.Read(Bodies.States, 0, (long)Bodies.States.Info.Size);
        resources.Read(Points!, 0, (long)Points!.Info.Size);
        if (transfer == DeviceInputTransfer.Upload)
        {
            resources.Write(References!, 0, (long)References!.Info.Size);
            resources.Read(_referenceUpload!, 0, (long)_referenceUpload!.Info.Size);
        }
        else resources.Read(References!, 0, (long)References!.Info.Size);
    }

    private void UploadReferences(CommandContext commands, DeviceInputTransfer transfer)
    {
        if (Bodies is null) return;
        if (!_referencesInitialized)
        {
            foreach (Buffer buffer in new[] { References!, Points! })
                _backend.Barrier(commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
            _referencesInitialized = true;
        }
        if (transfer != DeviceInputTransfer.Upload) return;
        _referenceMapping.Flush(_referenceMapping.Range);
        _backend.Barrier(commands, new BufferBarrier(References!, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(commands, new(_referenceUpload!, 0, References!, 0, References!.Info.Size));
        _backend.Barrier(commands, new BufferBarrier(References!, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }
}
