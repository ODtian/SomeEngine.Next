namespace SomeEngine.Assets;

/// <summary>A typed disk asset number. The default value denotes no asset.</summary>
public readonly struct AssetId<T> : IEquatable<AssetId<T>>
    where T : class
{
    public AssetId(Guid value) => Value = value;

    public static AssetId<T> Empty => default;
    public bool IsEmpty => Value == Guid.Empty;

    public Guid Value { get; }

    public bool IsValid => !IsEmpty;

    public bool Equals(AssetId<T> other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is AssetId<T> other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(AssetId<T> left, AssetId<T> right) => left.Equals(right);

    public static bool operator !=(AssetId<T> left, AssetId<T> right) => !left.Equals(right);

    public override string ToString() => IsValid ? $"{typeof(T).Name}:{Value}" : $"{typeof(T).Name}:Invalid";
}
