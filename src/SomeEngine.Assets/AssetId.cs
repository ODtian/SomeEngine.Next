using SomeEngine.Serialization;
using System.Text.Json;

namespace SomeEngine.Assets;

/// <summary>A typed disk asset number. The default value denotes no asset.</summary>
public readonly struct AssetId<T> : IEquatable<AssetId<T>>, IBinarySerializable<AssetId<T>>,
    IBinaryCustomViewContract<AssetId<T>>, ITextSerializable<AssetId<T>>
    where T : Asset
{
    public AssetId(Guid value) => Value = value;

    public static AssetId<T> Empty => default;
    public bool IsEmpty => Value == Guid.Empty;

    public Guid Value { get; }

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.AssetId:" + typeof(T).FullName);
    public static ulong SchemaFingerprint { get; } = BinaryFieldKey.FromName("SomeEngine.Assets.AssetId.Guid.v1");
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public static void Write(ref BinaryDataWriter writer, AssetId<T> value) => writer.WriteGuid(value.Value);
    public static AssetId<T> Read(ref BinaryDataReader reader) => new(reader.ReadGuid());
    public static void ValidateView(ref BinaryViewReader reader) => reader.ReadGuid();
    public static void WriteText(Utf8JsonWriter writer, AssetId<T> value) => writer.WriteStringValue(value.Value);
    public static AssetId<T> ReadText(JsonElement value) => new(value.GetGuid());

    public bool IsValid => !IsEmpty;

    public bool Equals(AssetId<T> other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is AssetId<T> other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(AssetId<T> left, AssetId<T> right) => left.Equals(right);

    public static bool operator !=(AssetId<T> left, AssetId<T> right) => !left.Equals(right);

    public override string ToString() => IsValid ? $"{typeof(T).Name}:{Value}" : $"{typeof(T).Name}:Invalid";
}
