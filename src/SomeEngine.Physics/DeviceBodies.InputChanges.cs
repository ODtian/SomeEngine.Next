using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodies
{
    private CpuInputChanges? _cpuInputChanges;

    internal void AddCpuInputBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        _cpuInputChanges ??= new(_backend, _device, Capacity);
        bindings.Add("DeviceBodyCpuInputChanges", [ResourceBinding.ReadOnlyBuffer(_cpuInputChanges.View)]);
    }
    internal JobResourceAccess[] GetCpuInputReadAccesses(Scheduler scheduler)
    {
        _cpuInputChanges ??= new(_backend, _device, Capacity);
        return [JobResourceAccess.Read(scheduler.GetResourceToken(_cpuInputChanges.Data), 0, (long)_cpuInputChanges.Data.Info.Size)];
    }
    private JobResourceAccess[] GetCpuInputTransferAccesses(Scheduler scheduler, bool writeInputs) =>
        _cpuInputChanges is { } changes ?
            [JobResourceAccess.Write(scheduler.GetResourceToken(changes.Data), 0, (long)changes.Data.Info.Size),
                writeInputs ? JobResourceAccess.Write(scheduler.GetResourceToken(changes.Upload), 0, (long)changes.Upload.Info.Size)
                    : JobResourceAccess.Read(scheduler.GetResourceToken(changes.Upload), 0, (long)changes.Upload.Info.Size)] : [];

    internal void RecordCpuInputChange(int index, Entity entity, BodyInputComponents components, long number, int substep) =>
        _cpuInputChanges?.Write(index, entity, components, number, substep);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CpuInputChange
    {
        [FieldOffset(0)] public uint EntityIndex;
        [FieldOffset(4)] public uint EntityGeneration;
        [FieldOffset(8)] public uint Components;
        [FieldOffset(12)] public uint Substep;
        [FieldOffset(16)] public long Number;
    }
    private sealed class CpuInputChanges : IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly MappedBuffer _mapping;
        private readonly List<BufferRange> _ranges = [];
        private bool _initialized;
        internal readonly Buffer Data, Upload;
        internal readonly BufferSrv View;

        internal CpuInputChanges(IGraphicsBackend backend, Device device, int capacity)
        {
            _backend = backend;
            Data = backend.CreateBuffer(device, new((ulong)capacity * 24,
                BufferUsages.ShaderRead | BufferUsages.CopyDestination, "CPU body input changes"), MemoryType.DeviceLocal);
            Upload = backend.CreateBuffer(device, new(Data.Info.Size, BufferUsages.CopySource), MemoryType.Upload);
            View = backend.CreateBufferSrv(device, new(Data, new(0, Data.Info.Size), StructureStride: 24));
            _mapping = backend.Map(Upload, MapType.Write, BufferRange.Whole);
            _mapping.Bytes.Clear();
        }
        internal void Write(int index, Entity entity, BodyInputComponents components, long number, int substep)
        {
            var value = new CpuInputChange { EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation,
                Components = (uint)components, Substep = (uint)substep, Number = number };
            MemoryMarshal.Write(_mapping.Bytes.Slice(index * 24, 24), in value);
            if (_initialized) _ranges.Add(new((ulong)index * 24, 24));
        }
        internal void Record(CommandContext commands)
        {
            if (!_initialized) _ranges.Add(new(0, Data.Info.Size));
            if (_ranges.Count == 0) return;
            _ranges.Sort(static (first, second) => first.Offset.CompareTo(second.Offset));
            int count = 0;
            foreach (BufferRange range in CollectionsMarshal.AsSpan(_ranges))
            {
                if (count > 0 && _ranges[count - 1].Offset + _ranges[count - 1].Size == range.Offset)
                {
                    BufferRange previous = _ranges[count - 1];
                    _ranges[count - 1] = new(previous.Offset, previous.Size + range.Size);
                }
                else _ranges[count++] = range;
            }
            _backend.Barrier(commands, new BufferBarrier(Data, _initialized ? PipelineSync.ComputeShading : Data.InitialSync,
                PipelineSync.Copy, _initialized ? ResourceAccess.ShaderResource : Data.InitialAccess, ResourceAccess.CopyDestination));
            foreach (BufferRange range in CollectionsMarshal.AsSpan(_ranges)[..count])
            {
                _mapping.Flush(range);
                _backend.CopyBuffer(commands, new(Upload, range.Offset, Data, range.Offset, range.Size));
            }
            _backend.Barrier(commands, new BufferBarrier(Data, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
            _initialized = true; _ranges.Clear();
        }
        public void Dispose() { _mapping.Dispose(); View.Dispose(); Upload.Dispose(); Data.Dispose(); }
    }
}
