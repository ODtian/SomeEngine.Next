using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct ContactIdentityKernels(ComputePipelineDesc Remove, ComputePipelineDesc Write);

public sealed partial class ContactIdentities<TCombine, TComponents>
    where TCombine : struct, IContactMaterialCombine where TComponents : struct, IContactIdentityComponents
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly Buffer _counts;
    private readonly BufferCbv _countsView;
    private Buffer _inputs;
    private BufferSrv _inputView;
    private MappedBuffer _inputMapping;
    private int _inputCount, _removedCount;
    private bool _inputInitialized;
    private const int InputStride = 52;

    private void BeginInputs(int capacity)
    {
        _inputCount = 0;
        _removedCount = 0;
        EnsureInputCapacity(capacity);
    }
    private void EnsureInputCapacity(int capacity)
    {
        ulong bytes = (ulong)Math.Max(1, capacity) * InputStride;
        if (_inputs.Info.Size >= bytes) return;
        Buffer replacement = _backend.CreateBuffer(_device, new((ulong)((capacity + 1023) / 1024 * 1024) * InputStride, BufferUsages.ShaderRead,
            "Contact identity input changes"), MemoryType.Upload);
        MappedBuffer mapping = _backend.Map(replacement, MapType.Write, BufferRange.Whole);
        _inputMapping.Bytes[..(_inputCount * InputStride)].CopyTo(mapping.Bytes);
        _inputMapping.Dispose(); _inputView.Dispose(); _inputs.Dispose();
        _inputs = replacement; _inputMapping = mapping;
        _inputView = _backend.CreateBufferSrv(_device, new(_inputs, new(0, _inputs.Info.Size), StructureStride: InputStride));
        _inputInitialized = false;
    }
    private void AddInputChange(int row, Entity point, uint warmStart, uint trigger, in ContactMaterial material, int detectedInput)
    {
        EnsureInputCapacity(_inputCount + 1);
        var change = new ContactInputChange((uint)row, (uint)detectedInput, point, _inputVersion, warmStart, trigger, material);
        MemoryMarshal.Write(_inputMapping.Bytes.Slice(_inputCount++ * InputStride, InputStride), in change);
    }
    private void SubmitInputs()
    {
        if (_inputCount == 0 && _removedCount == 0) return;
        Scheduler scheduler = SomeEngine.ECS.Systems.WorldJobs.GetScheduler(_world);
        JobResourceAccess[] accesses = [.. _parameters.GetDeviceWriteAccesses(scheduler), _matches.ReadAccess,
            .. _matches.GetIdentityWriteAccesses(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_inputs), 0, (long)_inputs.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_counts), 0, 256),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        DevicePhysicsJobs.Schedule(scheduler, new Transfer(this), accesses, default);
    }
    private QueueCompletion SubmitChanges()
    {
        if (_inputCount != 0) _inputMapping.Flush(new(0, (ulong)_inputCount * InputStride));
        using (MappedBuffer mapping = _backend.Map(_counts, MapType.Write, BufferRange.Whole))
        {
            Span<uint> counts = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            counts[0] = (uint)_inputCount; counts[1] = (uint)_removedCount;
            mapping.Flush(new(0, 8));
        }
        var bindings = new Dictionary<string, ResourceBinding[]>();
        _parameters.AddBindings(bindings); _matches.AddIdentityBindings(bindings);
        bindings.Add("ContactIdentityInputs", [ResourceBinding.ReadOnlyBuffer(_inputView)]);
        bindings.Add("ContactIdentityInputCounts", [ResourceBinding.ConstantBuffer(_countsView)]);
        _backend.Begin(_commands, new(0, 0, 8));
        _parameters.BeginDeviceWrite(_commands);
        if (!_inputInitialized) _backend.Barrier(_commands, new BufferBarrier(_inputs, _inputs.InitialSync,
            PipelineSync.ComputeShading, _inputs.InitialAccess, ResourceAccess.ShaderResource));
        int[] groups = [(_removedCount + 63) / 64, (_inputCount + 63) / 64];
        for (int kernel = 0; kernel < _pipelines.Length; kernel++)
        {
            if (groups[kernel] == 0) continue;
            _backend.SetPipeline(_commands, _pipelines[kernel]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], DeviceCollisionGrid.OrderBindings(_layouts[kernel], bindings), []));
            _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(groups[kernel]));
            _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        }
        using RecordedCommands recorded = _backend.End(_commands);
        _inputInitialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ContactInputChange(uint Row, uint DetectedInput, Entity Point, uint InputVersion, uint WarmStart,
        uint Trigger, ContactMaterial Material);
}
