namespace SomeEngine.Assets;

/// <summary>
/// Stable, type-safe identity used to request one concrete asset. The GUID remains the storage
/// identity; <typeparamref name="T"/> prevents a caller from requesting it as the wrong asset
/// type before I/O starts.
/// </summary>
public readonly struct AssetId<T> : IEquatable<AssetId<T>>
    where T : class
{
    public AssetId(AssetGuid value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("An asset ID cannot contain an empty GUID.", nameof(value));
        Value = value;
    }

    public AssetGuid Value { get; }

    public bool IsValid => !Value.IsEmpty;

    public bool Equals(AssetId<T> other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is AssetId<T> other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(AssetId<T> left, AssetId<T> right) => left.Equals(right);

    public static bool operator !=(AssetId<T> left, AssetId<T> right) => !left.Equals(right);

    public override string ToString() => IsValid ? $"{typeof(T).Name}:{Value}" : $"{typeof(T).Name}:Invalid";
}
