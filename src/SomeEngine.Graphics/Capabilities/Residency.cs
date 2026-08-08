namespace SomeEngine.Graphics;

public sealed class Residency : DeviceCapability
{
    internal Residency(Device device, bool localMemory, bool nonLocalMemory)
        : base(device)
    {
        LocalMemory = localMemory;
        NonLocalMemory = nonLocalMemory;
    }

    public bool LocalMemory { get; }
    public bool NonLocalMemory { get; }
}

public readonly record struct ResidencyInfo(
    ulong LocalBudget,
    ulong LocalUsage,
    ulong NonLocalBudget,
    ulong NonLocalUsage);

public readonly struct ResidencyResource : IEquatable<ResidencyResource>
{
    internal ResidencyResource(Device device, object value)
    {
        Device = device;
        Value = value;
    }

    public Device Device { get; }
    internal object? Value { get; }
    public bool IsDefault => Value is null;

    public bool Equals(ResidencyResource other) => ReferenceEquals(Value, other.Value);
    public override bool Equals(object? obj) => obj is ResidencyResource other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(ResidencyResource left, ResidencyResource right) => left.Equals(right);
    public static bool operator !=(ResidencyResource left, ResidencyResource right) => !left.Equals(right);
}
