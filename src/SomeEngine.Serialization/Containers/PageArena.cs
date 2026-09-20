using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Containers;

public sealed record PageArenaOptions
{
    public int MinimumPageSize { get; init; } = 256;
    public int MaximumPageSize { get; init; } = 64 * 1024;
    public int AllocationSize { get; init; } = 256 * 1024;
    public int RetainedEmptyAllocationsPerSize { get; init; } = 1;
}

/// <summary>Page capacities and copying statistics. The shared ledger remains the budget authority.</summary>
public readonly record struct PageArenaStatistics(
    long AllocatedBytes, long ActiveBytes, long RequestedBytes, long CopiedBytes, int ActivePages)
{
    public long IdleBytes => AllocatedBytes - ActiveBytes;
    /// <summary>Actual array element storage, excluding CLR headers and unused alignment capacity.</summary>
    public long BackingBytes { get; init; }
}

/// <summary>
/// Managed arrays subdivided into power-of-two byte capacities. Typed arrays preserve CLR
/// reference tracking. Unused tail capacity when a type does not divide its page size is
/// charged conservatively and reported separately from actual array element storage.
/// </summary>
public sealed class PageArena : IDisposable
{
    public static PageArena Shared { get; } = new(new ResidencyBudgetLedger());
    private readonly object _gate = new();
    private readonly Dictionary<(Type, int), IPageGroup> _groups = [];
    private readonly PageArenaOptions _options;
    private bool _disposed;
    private long _requestedBytes;
    private long _copiedBytes;

    public PageArena(ResidencyBudgetLedger residency, PageArenaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(residency);
        Residency = residency;
        _options = options ?? new();
        if (!BitOperations.IsPow2(_options.MinimumPageSize)
            || !BitOperations.IsPow2(_options.MaximumPageSize)
            || !BitOperations.IsPow2(_options.AllocationSize)
            || _options.MinimumPageSize > _options.MaximumPageSize
            || _options.MaximumPageSize > _options.AllocationSize)
            throw new ArgumentException("Page and allocation sizes must be increasing powers of two.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.RetainedEmptyAllocationsPerSize);
    }

    public ResidencyBudgetLedger Residency { get; }
    public int MaximumPageSize => _options.MaximumPageSize;

    public PageArenaStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                long allocated = 0, active = 0, backing = 0;
                int pages = 0;
                foreach (IPageGroup group in _groups.Values)
                {
                    var usage = group.GetUsage();
                    allocated += usage.Allocated;
                    active += usage.Active;
                    backing += usage.Backing;
                    pages += usage.Pages;
                }
                return new(allocated, active, _requestedBytes, Interlocked.Read(ref _copiedBytes), pages)
                { BackingBytes = backing };
            }
        }
    }

    internal int SizeFor(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(length));
        return Math.Max(_options.MinimumPageSize, (int)BitOperations.RoundUpToPowerOf2((uint)length));
    }

    internal long BufferCapacity(long length)
        => checked(length / MaximumPageSize * MaximumPageSize
            + (length % MaximumPageSize == 0 ? 0 : SizeFor((int)(length % MaximumPageSize))));

    /// <summary>Rounded page capacity required for a byte buffer of the given length.</summary>
    public long GetBufferCapacity(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return BufferCapacity(length);
    }

    internal Page<T> Rent<T>(int length, ResidencyReservation? funding = null)
    {
        int bytes = checked(length * Unsafe.SizeOf<T>());
        int size = SizeFor(bytes);
        ResidencyReservation? reservation;
        if (funding is not null) reservation = funding.Take(size);
        else if (!Residency.TryReserve(ResidencyClass.DecodedCpu, size, out reservation))
        {
            Residency.RequestReclaim(ResidencyClass.DecodedCpu, size);
            reservation = Residency.Reserve(ResidencyClass.DecodedCpu, size);
        }
        return RentReserved<T>(bytes, size, clear: true, reservation!);
    }

    internal Page<T> GetWritablePage<T>(Page<T> source)
    {
        int size = source.ByteCapacity;
        if (!Residency.TryReserve(ResidencyClass.DecodedCpu, size, out var reservation))
        {
            Residency.RequestReclaim(ResidencyClass.DecodedCpu, size);
            if (!source.IsShared) return source;
            reservation = Residency.Reserve(ResidencyClass.DecodedCpu, size);
        }
        int copied = source.Memory.Length * Unsafe.SizeOf<T>();
        Page<T> copy = RentReserved<T>(copied, size, clear: false, reservation!);
        source.Memory.Span.CopyTo(copy.Memory.Span);
        Interlocked.Add(ref _copiedBytes, copied);
        return copy;
    }

    private Page<T> RentReserved<T>(int bytes, int size, bool clear, ResidencyReservation reservation)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = (typeof(T), size);
                if (!_groups.TryGetValue(key, out IPageGroup? found))
                    _groups.Add(key, found = new PageGroup<T>());
                var group = (PageGroup<T>)found;
                PageAllocation<T>? allocation = group.Available.First?.Value;
                if (allocation is null)
                {
                    allocation = new PageAllocation<T>(_options.AllocationSize, size);
                    group.Allocations.Add(allocation);
                    allocation.AvailableNode = group.Available.AddLast(allocation);
                }
                int offset = allocation.Take();
                group.Empty.Remove(allocation);
                if (!allocation.HasSpace)
                {
                    group.Available.Remove(allocation.AvailableNode!);
                    allocation.AvailableNode = null;
                }
                var page = new Page<T>(this, allocation, offset, reservation);
                if (clear) page.Memory.Span.Clear();
                _requestedBytes += bytes;
                return page;
            }
        }
        catch { reservation.Dispose(); throw; }
    }

    internal void Return<T>(PageAllocation<T> allocation, int offset)
    {
        lock (_gate)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                allocation.Values.AsSpan(offset, allocation.PageLength).Clear();
            var group = (PageGroup<T>)_groups[(typeof(T), allocation.PageSize)];
            allocation.Return(offset);
            if (allocation.AvailableNode is null)
                allocation.AvailableNode = group.Available.AddFirst(allocation);
            if (allocation.Count != 0) return;
            group.Empty.Add(allocation);
            if (_disposed || group.Empty.Count > _options.RetainedEmptyAllocationsPerSize)
                group.Remove(allocation);
        }
    }

    public void Trim()
    {
        lock (_gate)
            foreach (IPageGroup group in _groups.Values) group.Trim();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Trim(); }
    }

    private interface IPageGroup
    {
        (long Allocated, long Active, long Backing, int Pages) GetUsage();
        void Trim();
    }

    private sealed class PageGroup<T> : IPageGroup
    {
        internal readonly HashSet<PageAllocation<T>> Allocations = [];
        internal readonly LinkedList<PageAllocation<T>> Available = [];
        internal readonly HashSet<PageAllocation<T>> Empty = [];
        internal void Remove(PageAllocation<T> allocation)
        {
            Allocations.Remove(allocation);
            Empty.Remove(allocation);
            Available.Remove(allocation.AvailableNode!);
            allocation.AvailableNode = null;
        }
        public void Trim() { while (Empty.Count != 0) Remove(Empty.First()); }
        public (long Allocated, long Active, long Backing, int Pages) GetUsage()
        {
            long allocated = 0, active = 0, backing = 0;
            int pages = 0;
            foreach (PageAllocation<T> allocation in Allocations)
            {
                allocated += allocation.ByteCapacity;
                active += (long)allocation.Count * allocation.PageSize;
                backing += allocation.Values.LongLength * Unsafe.SizeOf<T>();
                pages += allocation.Count;
            }
            return (allocated, active, backing, pages);
        }
    }
}

internal sealed class PageAllocation<T>
{
    private readonly Stack<int> _free = [];
    private int _next;
    internal PageAllocation(int bytes, int pageSize)
    {
        ByteCapacity = bytes;
        PageSize = pageSize;
        PageLength = pageSize / Unsafe.SizeOf<T>();
        Values = new T[bytes / pageSize * PageLength];
    }
    internal readonly T[] Values;
    internal readonly int ByteCapacity, PageSize, PageLength;
    internal int Count { get; private set; }
    internal LinkedListNode<PageAllocation<T>>? AvailableNode;
    internal bool HasSpace => _free.Count != 0 || _next < Values.Length;
    internal int Take()
    {
        int offset;
        if (_free.TryPop(out offset)) { }
        else { offset = _next; _next += PageLength; }
        Count++;
        return offset;
    }
    internal void Return(int offset) { Count--; _free.Push(offset); }
}

internal sealed class Page<T>(PageArena arena, PageAllocation<T> allocation, int offset, ResidencyReservation reservation)
{
    private int _references = 1;
    internal Memory<T> Memory => allocation.Values.AsMemory(offset, allocation.PageLength);
    internal int ByteCapacity => allocation.PageSize;
    internal bool IsShared => Volatile.Read(ref _references) != 1;
    internal Page<T> Retain() { Interlocked.Increment(ref _references); return this; }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        arena.Return(allocation, offset);
        reservation.Dispose();
    }
}
