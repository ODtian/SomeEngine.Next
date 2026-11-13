using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeCommandData? GetCommandData(CommandContext? context, ref ID3D12GraphicsCommandList10* target)
    {
        if (context is null) return null;
        D3D12CommandBuffer recording = ((D3D12CommandContext)context).Recording;
        if (recording.CollectingCommands) return recording.CommandData;
        target = recording.List;
        return null;
    }

    internal static ID3D12GraphicsCommandList10* GetNativeRecording(CommandContext? context, ID3D12GraphicsCommandList10* target)
    {
        if (context is null) return target;
        D3D12CommandBuffer recording = ((D3D12CommandContext)context).Recording;
        recording.FlushCollectedCommands();
        return recording.List;
    }

    private sealed class CachedNativeCommands
    {
        internal ID3D12CommandAllocator* Allocator;
        internal ID3D12GraphicsCommandList10* List;
        internal NativeCommandData Data = new();
        internal readonly List<object> References = [];
        internal bool Complete;
        private ID3D12DescriptorHeap* _resources;
        private ID3D12DescriptorHeap* _samplers;

        internal void Invalidate()
        {
            Complete = false;
            References.Clear();
            if (_resources is not null)
                _ = ResourceTrace.Release((IUnknown*)_resources);
            if (_samplers is not null)
                _ = ResourceTrace.Release((IUnknown*)_samplers);
            _resources = null;
            _samplers = null;
        }

        internal void StoreReferences(List<object> references, ID3D12DescriptorHeap* resources, ID3D12DescriptorHeap* samplers)
        {
            References.AddRange(references);
            // Retain only descriptor heaps. Resource identities are managed references;
            // the current recording retains their native lifetimes until completion.
            // Keeping heaps alive also prevents false matches after native address reuse.
            _resources = resources;
            _samplers = samplers;
            if (resources is not null)
                _ = resources->AddRef();
            if (samplers is not null)
                _ = samplers->AddRef();
            Complete = true;
        }
    }

    private sealed partial class D3D12CommandBuffer
    {
        private readonly CachedNativeCommands?[] _cachedCommands = new CachedNativeCommands?[2];
        private readonly List<object> _commandReferences = [];
        private NativeCommandData _commandData = new();
        private CachedNativeCommands _nativeRecording;
        private int _nextCommands;
        private bool _collectingCommands;
        private bool _nativeRecordingOpen;

        internal bool CollectingCommands => _collectingCommands;
        internal NativeCommandData CommandData => _commandData;

        private void BeginCommandData(bool repeatable)
        {
            _repeatParameters.Clear();
            _commandData.Clear();
            _commandReferences.Clear();
            _nativeRecordingOpen = false;
            _collectingCommands = repeatable && !_context.Bundle;
            if (!_collectingCommands)
                FlushCollectedCommands();
        }

        internal void FinishCommandData()
        {
            bool reusable = _collectingCommands &&
                _transientObjects.Count == 0 &&
                SwapchainUseCount == 0 &&
                _temporaryRtvUsed == 0 && _temporaryDsvUsed == 0;
            if (reusable)
            {
                foreach (D3D12PersistentParameterData data in _capturedParameterData)
                    _commandReferences.Add(data);
                if (_descriptorGeneration is not null)
                    _commandReferences.Add(_descriptorGeneration);
                foreach (D3D12OrdinaryDataChunk chunk in _ordinaryDataChunks)
                    _commandReferences.Add(chunk);
            }

            if (_collectingCommands)
                FlushCollectedCommands();
            ThrowIfFailed(_context.NativeDevice, CommandExecutionTrace.Close(List),
                NativeOperationType.Ordinary, "ID3D12GraphicsCommandList::Close");
            _nativeRecordingOpen = false;
            if (reusable)
            {
                CachedNativeCommands cached = _nativeRecording;
                cached.StoreReferences(_commandReferences, _resourceArena, _samplerArena);
                (cached.Data, _commandData) = (_commandData, cached.Data);
            }
            FinishCommandRecording();
        }

        private void FinishCommandRecording()
        {
            _collectingCommands = false;
            _commandReferences.Clear();
        }

        internal void AbortCommandData()
        {
            if (_nativeRecordingOpen)
                _ = CommandExecutionTrace.Close(List);
            _nativeRecordingOpen = false;
            _commandData.Clear();
            FinishCommandRecording();
        }

        internal void FlushCollectedCommands()
        {
            if (_nativeRecordingOpen) return;
            using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.EncodeNativeCommands);
            CachedNativeCommands cached = _cachedCommands[_nextCommands] ??= CreateCachedCommands();
            _nextCommands = (_nextCommands + 1) % _cachedCommands.Length;
            cached.Invalidate();
            _nativeRecording = cached;
            ThrowIfFailed(_context.NativeDevice, ResourceTrace.Reset(cached.Allocator),
                NativeOperationType.Ordinary, "ID3D12CommandAllocator::Reset");
            ThrowIfFailed(_context.NativeDevice, CommandExecutionTrace.Reset(cached.List, cached.Allocator, null),
                NativeOperationType.Ordinary, "ID3D12GraphicsCommandList::Reset");
            _nativeRecordingOpen = true;
            _collectingCommands = false;
            _commandData.Execute(cached.List);
        }

        private CachedNativeCommands CreateCachedCommands()
        {
            D3D12Device device = _context.NativeDevice;
            CommandListType type = _context.Bundle ? CommandListType.Bundle : ToCommandListType(_context.QueueType);
            Guid allocatorIid = ID3D12CommandAllocator.Guid;
            ID3D12CommandAllocator* allocator = null;
            ID3D12GraphicsCommandList10* list = null;
            ThrowIfFailed(device, DevicePipelineTrace.CreateCommandAllocator(device.Native, type, &allocatorIid, (void**)&allocator),
                NativeOperationType.Ordinary, "ID3D12Device::CreateCommandAllocator");
            try
            {
                Guid listIid = ID3D12GraphicsCommandList10.Guid;
                ThrowIfFailed(device, DevicePipelineTrace.CreateCommandList1(device.Native, _context.NativeNodeMask,
                    type, CommandListFlags.None, &listIid, (void**)&list),
                    NativeOperationType.Ordinary, "ID3D12Device4::CreateCommandList1");
                string name = _context.Label ?? $"{_context.QueueType} CommandContext[{_context.QueueIndex}]";
                SetNativeName(allocator, $"{name} Allocator");
                SetNativeName(list, $"{name} Command List");
                CachedNativeCommands commands = new() { Allocator = allocator, List = list };
                return commands;
            }
            catch
            {
                if (list is not null)
                    _ = CommandExecutionTrace.Release(list);
                _ = ResourceTrace.Release(allocator);
                throw;
            }
        }

        private void ReleaseCachedCommands()
        {
            _repeatParameters.Clear();
            FinishCommandRecording();
            foreach (CachedNativeCommands? cached in _cachedCommands)
            {
                if (cached is null)
                    continue;
                cached.Invalidate();
                _ = CommandExecutionTrace.Release(cached.List);
                _ = ResourceTrace.Release(cached.Allocator);
            }
            Array.Clear(_cachedCommands);
            _nativeRecording = null!;
        }
    }
}
