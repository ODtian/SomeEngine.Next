using System.Threading;

namespace SomeEngine.Graphics;

/// <summary>
/// Opaque identity for one physical graphics-memory allocation. Placed resources created from the
/// same heap carry the same identity; committed resources each receive a distinct identity.
/// </summary>
public readonly struct PhysicalAllocationId : IEquatable<PhysicalAllocationId>
{
    private static long s_next;
    private readonly ulong _value;

    private PhysicalAllocationId(DeviceDomain domain, ulong value)
    {
        Domain = domain;
        _value = value;
    }

    public DeviceDomain Domain { get; }
    public bool IsValid => Domain.IsValid && _value != 0;

    public bool Equals(PhysicalAllocationId other) => Domain == other.Domain && _value == other._value;
    public override bool Equals(object? obj) => obj is PhysicalAllocationId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Domain, _value);
    public override string ToString() => IsValid ? nameof(PhysicalAllocationId) : $"Invalid {nameof(PhysicalAllocationId)}";

    public static bool operator ==(PhysicalAllocationId left, PhysicalAllocationId right) => left.Equals(right);
    public static bool operator !=(PhysicalAllocationId left, PhysicalAllocationId right) => !left.Equals(right);

    internal static PhysicalAllocationId Allocate(DeviceDomain domain)
    {
        if (!domain.IsValid) throw new ArgumentException("A valid device domain is required.", nameof(domain));
        while (true)
        {
            ulong value = unchecked((ulong)Interlocked.Increment(ref s_next));
            if (value != 0) return new PhysicalAllocationId(domain, value);
        }
    }
}

/// <summary>The byte range occupied by a resource inside one physical allocation.</summary>
public readonly record struct PhysicalAllocationInfo
{
    public PhysicalAllocationInfo(PhysicalAllocationId identity, ulong offset, ulong size)
    {
        if (!identity.IsValid) throw new ArgumentException("A valid physical allocation identity is required.", nameof(identity));
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        _ = checked(offset + size);
        Identity = identity;
        Offset = offset;
        Size = size;
    }

    public PhysicalAllocationId Identity { get; }
    public ulong Offset { get; }
    public ulong Size { get; }
    public ulong End => checked(Offset + Size);
}

/// <summary>Immutable structural, memory, and physical-allocation metadata for a live buffer.</summary>
public readonly record struct BufferMetadata(
    BufferDesc Description,
    MemoryType MemoryType,
    PhysicalAllocationInfo Allocation);

/// <summary>Immutable structural, memory, and physical-allocation metadata for a live texture.</summary>
public readonly record struct TextureMetadata(
    TextureDesc Description,
    MemoryType MemoryType,
    PhysicalAllocationInfo Allocation);
