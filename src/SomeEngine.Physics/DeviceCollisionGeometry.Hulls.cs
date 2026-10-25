using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

internal readonly record struct DeviceHullRange(int VertexStart, int FaceStart, int FaceIndexStart);

public sealed partial class DeviceCollisionGeometry
{
    private readonly ConcurrentDictionary<ConvexHullData, DeviceHullRange> _hulls = [];
    private readonly object _hullGate = new();
    private readonly List<HullTransfer> _hullTransfers = [];
    private int _vertices;
    private int _faces;
    private int _faceIndices;
    private int _uploadedVertices;
    private int _uploadedFaces;
    private int _uploadedFaceIndices;
    private bool _hullsInitialized;

    internal void RegisterHull(ConvexHullData data)
    {
        if (_hulls.ContainsKey(data)) return;
        lock (_hullGate)
        {
            if (_hulls.ContainsKey(data)) return;
            var range = new DeviceHullRange(_vertices, _faces, _faceIndices);
            MemoryMarshal.AsBytes(data.Vertices.Span).CopyTo(_hullTransfers[0].Mapping.Bytes[(_vertices * 12)..]);
            MemoryMarshal.AsBytes(data.Faces.Span).CopyTo(_hullTransfers[1].Mapping.Bytes[(_faces * 20)..]);
            MemoryMarshal.AsBytes(data.FaceVertices.Span).CopyTo(_hullTransfers[2].Mapping.Bytes[(_faceIndices * 4)..]);
            _vertices += data.Vertices.Length;
            _faces += data.Faces.Length;
            _faceIndices += data.FaceVertices.Length;
            _hulls.TryAdd(data, range);
        }
    }

    internal DeviceHullRange GetHullRange(ConvexHullData data) => _hulls[data];

    internal void CollectHullUploadAccesses(ref JobResourceAccessBuilder resources)
    {
        foreach (HullTransfer transfer in _hullTransfers)
            resources.Write(transfer.Upload, 0, (long)transfer.Upload.Info.Size);
    }

    private void CreateHullTransfers(Device device)
    {
        if (Capacity.Vertices == 0) return;
        CreateHullTransfer(device, "HullVertices", (ulong)Capacity.Vertices * 12, 12);
        CreateHullTransfer(device, "HullFaces", (ulong)Capacity.Faces * 20, 20);
        CreateHullTransfer(device, "HullFaceIndices", (ulong)Capacity.FaceIndices * 4, 4);
    }

    private void CreateHullTransfer(Device device, string name, ulong bytes, uint stride)
    {
        Buffer buffer = Keep(_backend.CreateBuffer(device, new(bytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, name), MemoryType.DeviceLocal));
        BufferUav view = Keep(_backend.CreateBufferUav(device, new(buffer, new(0, bytes), StructureStride: stride)));
        Buffer upload = Keep(_backend.CreateBuffer(device, new(bytes, BufferUsages.CopySource, name + " upload"), MemoryType.Upload));
        _hullTransfers.Add(new(name, buffer, view, upload, _backend.Map(upload, MapType.Write, BufferRange.Whole)));
    }

    private void CollectHullAccesses(DeviceInputTransfer transfer, ref JobResourceAccessBuilder resources)
    {
        foreach (HullTransfer hull in _hullTransfers)
        {
            if (transfer == DeviceInputTransfer.Upload)
            {
                resources.Write(hull.Buffer, 0, (long)hull.Buffer.Info.Size);
                resources.Read(hull.Upload, 0, (long)hull.Upload.Info.Size);
            }
            else resources.Read(hull.Buffer, 0, (long)hull.Buffer.Info.Size);
        }
    }

    private void UploadHullInputs(CommandContext commands, DeviceInputTransfer transfer)
    {
        if (!_hullsInitialized)
        {
            foreach (HullTransfer hull in _hullTransfers)
                _backend.Barrier(commands, new BufferBarrier(hull.Buffer, hull.Buffer.InitialSync, PipelineSync.ComputeShading,
                    hull.Buffer.InitialAccess, ResourceAccess.UnorderedAccess));
            _hullsInitialized = true;
        }
        if (transfer != DeviceInputTransfer.Upload) return;
        UploadHullRange(commands, 0, _uploadedVertices * 12, (_vertices - _uploadedVertices) * 12);
        UploadHullRange(commands, 1, _uploadedFaces * 20, (_faces - _uploadedFaces) * 20);
        UploadHullRange(commands, 2, _uploadedFaceIndices * 4, (_faceIndices - _uploadedFaceIndices) * 4);
        _uploadedVertices = _vertices;
        _uploadedFaces = _faces;
        _uploadedFaceIndices = _faceIndices;
    }

    private void UploadHullRange(CommandContext commands, int index, int start, int count)
    {
        if (count == 0) return;
        HullTransfer hull = _hullTransfers[index];
        var range = new BufferRange((ulong)start, (ulong)count);
        hull.Mapping.Flush(range);
        _backend.Barrier(commands, new BufferBarrier(hull.Buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(commands, new BufferCopy(hull.Upload, (ulong)start, hull.Buffer, (ulong)start, (ulong)count));
        _backend.Barrier(commands, new BufferBarrier(hull.Buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }

    private readonly record struct HullTransfer(string Name, Buffer Buffer, BufferUav View, Buffer Upload, MappedBuffer Mapping);
}
