using System.Reflection;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static unsafe partial class D3D12PrivateState
{
    internal static bool HasNativeDevice(Device device) =>
        PointerPropertyIsNonZero(device, "Native");

    internal static bool HasNativeResource(Resource resource) =>
        PointerPropertyIsNonZero(resource, "Native");

    internal static ulong CountSparseMappingTiles(
        Resource resource,
        in SparseTileRegion region,
        Heap? heap)
    {
        object state = InvokeStatic("GetSparseState", resource)!;
        object logicalRegion = Invoke(state, "PrepareRegion", region)!;
        object? lifetime = heap is null
            ? null
            : GetProperty(heap, "NativeLifetime").GetValue(heap);
        object generation = GetField(state, "_current").GetValue(state)!;
        Array ranges = (Array)GetField(generation, "_ranges").GetValue(generation)!;
        int rangeCount = (int)GetField(generation, "_rangeCount").GetValue(generation)!;
        object enumerator = Invoke(logicalRegion, "GetEnumerator")!;
        ulong result = 0;
        while ((bool)Invoke(enumerator, "MoveNext")!)
        {
            object interval = GetProperty(enumerator, "Current").GetValue(enumerator)!;
            uint segment = (uint)GetProperty(interval, "Segment").GetValue(interval)!;
            ulong start = (ulong)GetProperty(interval, "Start").GetValue(interval)!;
            ulong tileCount = (ulong)GetProperty(interval, "TileCount").GetValue(interval)!;
            ulong end = checked(start + tileCount);
            ulong mapped = 0;
            for (int index = 0; index < rangeCount; index++)
            {
                object mapping = ranges.GetValue(index)!;
                if ((uint)GetProperty(mapping, "Segment").GetValue(mapping)! != segment)
                    continue;
                ulong mappingStart = (ulong)GetProperty(mapping, "Start").GetValue(mapping)!;
                ulong mappingEnd = (ulong)GetProperty(mapping, "End").GetValue(mapping)!;
                ulong overlapStart = Math.Max(start, mappingStart);
                ulong overlapEnd = Math.Min(end, mappingEnd);
                if (overlapStart >= overlapEnd)
                    continue;
                ulong overlap = overlapEnd - overlapStart;
                mapped += overlap;
                object mappingHeap = GetProperty(mapping, "Heap").GetValue(mapping)!;
                if (lifetime is not null && ReferenceEquals(mappingHeap, lifetime))
                    result += overlap;
            }
            if (lifetime is null)
                result += tileCount - mapped;
        }
        return result;
    }

    internal static nint NativeHeapPointer(Heap heap)
    {
        object lifetime = GetProperty(heap, "NativeLifetime").GetValue(heap)!;
        return (nint)GetProperty(lifetime, "Pointer").GetValue(lifetime)!;
    }

    internal static PooledResourceAllocation? PooledAllocation(Resource resource)
    {
        object? allocation = GetProperty(resource, "MemoryAllocation").GetValue(resource);
        if (allocation is null)
            return null;
        object block = GetProperty(allocation, "Block").GetValue(allocation)!;
        object lifetime = GetProperty(block, "NativeLifetime").GetValue(block)!;
        return new PooledResourceAllocation(
            (nint)GetProperty(lifetime, "Pointer").GetValue(lifetime)!,
            (ulong)GetProperty(allocation, "Offset").GetValue(allocation)!,
            (ulong)GetProperty(allocation, "Size").GetValue(allocation)!);
    }

    internal static int NativeLeaseReferenceCount(GraphicsObject value)
    {
        object lifetime = NativeLeaseObject(value);
        return NativeLeaseReferenceCount(lifetime);
    }

    internal static object NativeLeaseObject(GraphicsObject value) =>
        GetProperty(value, "NativeLifetime").GetValue(value)!;

    internal static int NativeLeaseReferenceCount(object lifetime) =>
        (int)GetField(lifetime, "_references").GetValue(lifetime)!;

    internal static int DisposeGateState(GraphicsObject value)
    {
        FieldInfo gateField = GetField(value, "_disposeGate");
        object gate = gateField.GetValue(value)!;
        return (int)GetField(gate, "_state").GetValue(gate)!;
    }
}

internal readonly record struct PooledResourceAllocation(
    nint HeapPointer,
    ulong Offset,
    ulong Size);
