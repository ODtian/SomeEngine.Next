using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private sealed class CommandDescriptorHeap : IDisposable
    {
        private ID3D12DescriptorHeap* _heap;

        internal CommandDescriptorHeap(
            D3D12Device device,
            uint nodeMask,
            ParameterHeap type,
            uint capacity)
        {
            Capacity = capacity;
            Contents = new DescriptorContent[checked((int)capacity)];
            DescriptorHeapDesc description = new(
                type == ParameterHeap.Resource
                    ? DescriptorHeapType.CbvSrvUav
                    : DescriptorHeapType.Sampler,
                capacity,
                DescriptorHeapFlags.ShaderVisible,
                nodeMask);
            Guid iid = ID3D12DescriptorHeap.Guid;
            ID3D12DescriptorHeap* heap = null;
            ThrowIfFailed(
                device,
                DeviceResourceTrace.CreateDescriptorHeap(
                    device.Native,
                    &description,
                    &iid,
                    (void**)&heap),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap(command descriptors)");
            _heap = heap;
            CpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
            GpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(heap);
        }

        internal ID3D12DescriptorHeap* Heap => _heap;
        internal uint Capacity { get; }
        internal CpuDescriptorHandle CpuStart { get; }
        internal GpuDescriptorHandle GpuStart { get; }
        internal DescriptorContent[] Contents { get; }
        internal ulong PublishedDescriptorIdentity { get; set; }

        public void Dispose()
        {
            ID3D12DescriptorHeap* heap = _heap;
            _heap = null;
            if (heap is not null)
                _ = ResourceTrace.Release(heap);
        }
    }

    private sealed partial class D3D12CommandSlot
    {
        private readonly List<CommandDescriptorHeap> _resourceDescriptorHeaps = [];
        private readonly List<CommandDescriptorHeap> _samplerDescriptorHeaps = [];
        private CommandDescriptorHeap? _resourceDescriptors;
        private CommandDescriptorHeap? _samplerDescriptors;
        private int _resourceHeapsUsed;
        private int _samplerHeapsUsed;

        private ID3D12DescriptorHeap* ResourceDescriptorHeap =>
            _resourceDescriptors is null ? null : _resourceDescriptors.Heap;

        private ID3D12DescriptorHeap* SamplerDescriptorHeap =>
            _samplerDescriptors is null ? null : _samplerDescriptors.Heap;

        internal uint ResourceDescriptorCapacity => _resourceDescriptors?.Capacity ?? 0;
        internal uint SamplerDescriptorCapacity => _samplerDescriptors?.Capacity ?? 0;

        private CommandDescriptorHeap TakeDescriptorHeap(ParameterHeap type, uint capacity)
        {
            bool sampler = type == ParameterHeap.Sampler;
            List<CommandDescriptorHeap> heaps = sampler ? _samplerDescriptorHeaps : _resourceDescriptorHeaps;
            int used = sampler ? _samplerHeapsUsed : _resourceHeapsUsed;
            int selected = used;
            while (selected < heaps.Count && heaps[selected].Capacity < capacity)
                selected++;

            CommandDescriptorHeap descriptors;
            if (selected < heaps.Count)
            {
                descriptors = heaps[selected];
                heaps[selected] = heaps[used];
                heaps[used] = descriptors;
            }
            else
            {
                heaps.EnsureCapacity(checked(used + 1));
                descriptors = new CommandDescriptorHeap(
                    _context.NativeDevice,
                    _context.NativeNodeMask,
                    type,
                    capacity);
                if (used < heaps.Count)
                {
                    heaps[used].Dispose();
                    heaps[used] = descriptors;
                }
                else
                {
                    heaps.Add(descriptors);
                }
            }

            if (sampler)
                _samplerHeapsUsed = used + 1;
            else
                _resourceHeapsUsed = used + 1;
            return descriptors;
        }

        private void CopyDescriptorPrefix(
            ParameterHeap type,
            CommandDescriptorHeap destination,
            DescriptorGeneration descriptors)
        {
            if (destination.PublishedDescriptorIdentity == descriptors.Identity)
                return;
            uint count;
            if (type == ParameterHeap.Sampler)
            {
                descriptors.CopySamplerTo(_context.NativeDevice, destination.Heap);
                count = descriptors.SamplerCount;
            }
            else
            {
                descriptors.CopyResourceTo(_context.NativeDevice, destination.Heap);
                count = descriptors.ResourceCount;
            }
            Array.Clear(destination.Contents, 0, checked((int)count));
            destination.PublishedDescriptorIdentity = descriptors.Identity;
        }

        private void AppendDescriptorHeaps(List<object> references)
        {
            for (int index = 0; index < _resourceHeapsUsed; index++)
                references.Add(_resourceDescriptorHeaps[index]);
            for (int index = 0; index < _samplerHeapsUsed; index++)
                references.Add(_samplerDescriptorHeaps[index]);
        }
    }
}
