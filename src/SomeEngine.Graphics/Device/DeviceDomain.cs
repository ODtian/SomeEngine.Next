using System.Threading;

namespace SomeEngine.Graphics;

/// <summary>
/// Opaque identity for one graphics execution domain. A domain can be copied and compared, but
/// only a graphics backend can allocate a valid identity.
/// </summary>
public readonly struct DeviceDomain : IEquatable<DeviceDomain>
{
    private static long s_next;
    private readonly ulong _value;

    private DeviceDomain(ulong value) => _value = value;

    public bool IsValid => _value != 0;

    public bool Equals(DeviceDomain other) => _value == other._value;
    public override bool Equals(object? obj) => obj is DeviceDomain other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => IsValid ? nameof(DeviceDomain) : $"Invalid {nameof(DeviceDomain)}";

    public static bool operator ==(DeviceDomain left, DeviceDomain right) => left.Equals(right);
    public static bool operator !=(DeviceDomain left, DeviceDomain right) => !left.Equals(right);

    internal static DeviceDomain Allocate()
    {
        while (true)
        {
            ulong value = unchecked((ulong)Interlocked.Increment(ref s_next));
            if (value != 0) return new DeviceDomain(value);
        }
    }
}
