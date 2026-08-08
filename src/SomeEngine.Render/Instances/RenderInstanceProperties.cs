using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Opaque identity shared by a property producer and its shader consumer. The render-instance
/// system compares keys but never assigns business meaning to them.
/// </summary>
public readonly struct RenderInstancePropertyKey : IEquatable<RenderInstancePropertyKey>
{
    private readonly string? _value;

    public RenderInstancePropertyKey(string value)
    {
        Validate(value);
        _value = value;
    }

    public bool IsValid => _value is not null;

    public string Value =>
        _value ?? throw new InvalidOperationException("The render-instance property key is uninitialized.");

    public bool Equals(RenderInstancePropertyKey other) =>
        StringComparer.Ordinal.Equals(_value, other._value);

    public override bool Equals(object? obj) =>
        obj is RenderInstancePropertyKey other && Equals(other);

    public override int GetHashCode() =>
        _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public override string ToString() => _value ?? "<uninitialized>";

    public static bool operator ==(RenderInstancePropertyKey left, RenderInstancePropertyKey right) =>
        left.Equals(right);

    public static bool operator !=(RenderInstancePropertyKey left, RenderInstancePropertyKey right) =>
        !left.Equals(right);

    private static void Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        bool segmentHasCharacter = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '.')
            {
                if (!segmentHasCharacter)
                    throw new ArgumentException("Property-key segments must not be empty.", nameof(value));
                segmentHasCharacter = false;
                continue;
            }

            if (!(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character != '_')
            {
                throw new ArgumentException(
                    "Property keys may contain lowercase ASCII letters, digits, underscores, and dots only.",
                    nameof(value));
            }
            segmentHasCharacter = true;
        }

        if (!segmentHasCharacter)
            throw new ArgumentException("Property-key segments must not be empty.", nameof(value));
    }
}

/// <summary>
/// Opaque byte/metadata agreement between a producer and a shader reader. A positive storage
/// stride opts into the shared linear store; zero leaves all metadata interpretation to the
/// producer and consumer. The stride is always authored explicitly and is never inferred here.
/// </summary>
public readonly struct RenderInstancePropertyEncoding : IEquatable<RenderInstancePropertyEncoding>
{
    private readonly string? _codec;

    public RenderInstancePropertyEncoding(
        string codec,
        int valueSize,
        int storageAlignment,
        int storageStride,
        int metadataWordCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codec);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageAlignment);
        ArgumentOutOfRangeException.ThrowIfNegative(storageStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadataWordCount);
        if (!IsPowerOfTwo(storageAlignment))
            throw new ArgumentException("Storage alignment must be a power of two.", nameof(storageAlignment));
        if (storageStride != 0
            && (storageStride < valueSize || storageStride % storageAlignment != 0))
        {
            throw new ArgumentException(
                "A managed storage stride must contain the value and be a multiple of its alignment.",
                nameof(storageStride));
        }

        _codec = codec;
        ValueSize = valueSize;
        StorageAlignment = storageAlignment;
        StorageStride = storageStride;
        MetadataWordCount = metadataWordCount;
    }

    public bool IsValid => _codec is not null;

    /// <summary>Canonical producer/shader codec agreement; it is not a runtime identity.</summary>
    public string Codec =>
        _codec ?? throw new InvalidOperationException("The render-instance property encoding is uninitialized.");

    public int ValueSize { get; }

    public int StorageAlignment { get; }

    public int StorageStride { get; }

    public int MetadataWordCount { get; }

    public bool HasManagedStorage => StorageStride != 0;

    public bool Equals(RenderInstancePropertyEncoding other) =>
        StringComparer.Ordinal.Equals(_codec, other._codec)
        && ValueSize == other.ValueSize
        && StorageAlignment == other.StorageAlignment
        && StorageStride == other.StorageStride
        && MetadataWordCount == other.MetadataWordCount;

    public override bool Equals(object? obj) =>
        obj is RenderInstancePropertyEncoding other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        _codec is null ? 0 : StringComparer.Ordinal.GetHashCode(_codec),
        ValueSize,
        StorageAlignment,
        StorageStride,
        MetadataWordCount);

    public static bool operator ==(
        RenderInstancePropertyEncoding left,
        RenderInstancePropertyEncoding right) => left.Equals(right);

    public static bool operator !=(
        RenderInstancePropertyEncoding left,
        RenderInstancePropertyEncoding right) => !left.Equals(right);

    internal void ValidateManagedType<T>(string parameterName)
        where T : unmanaged
    {
        if (!IsValid)
            throw new ArgumentException("The property encoding is uninitialized.", parameterName);
        int managedSize = Unsafe.SizeOf<T>();
        if (managedSize != ValueSize)
        {
            throw new ArgumentException(
                $"Managed value '{typeof(T).FullName}' is {managedSize} bytes, but encoding '{Codec}' " +
                $"declares {ValueSize} bytes.",
                parameterName);
        }
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
}

/// <summary>
/// The standard one-word linear-store metadata convention offered by the generic system. Business
/// readers may use it or bind and interpret their own metadata words.
/// </summary>
public static class RenderInstanceLinearMetadata
{
    public const uint PerInstanceBit = 0x80000000u;
    public const uint AddressMask = 0x7fffffffu;
}

/// <summary>A typed property token independent of any particular composed metadata layout.</summary>
public readonly struct RenderInstanceProperty<T> : IEquatable<RenderInstanceProperty<T>>
    where T : unmanaged
{
    private readonly RenderInstancePropertyDeclaration? _declaration;

    internal RenderInstanceProperty(RenderInstancePropertyDeclaration declaration)
    {
        _declaration = declaration;
    }

    public bool IsValid => _declaration is not null;

    public RenderInstancePropertyKey Key => Declaration.Key;

    public RenderInstancePropertyEncoding Encoding => Declaration.Encoding;

    internal RenderInstancePropertyDeclaration Declaration =>
        _declaration ?? throw new InvalidOperationException("The render-instance property token is uninitialized.");

    public bool Equals(RenderInstanceProperty<T> other) =>
        ReferenceEquals(_declaration, other._declaration)
        || (_declaration is not null
            && other._declaration is not null
            && _declaration.HasSameContract(other._declaration));

    public override bool Equals(object? obj) =>
        obj is RenderInstanceProperty<T> other && Equals(other);

    public override int GetHashCode() =>
        _declaration is null ? 0 : HashCode.Combine(_declaration.Key, _declaration.Encoding);

    public static bool operator ==(RenderInstanceProperty<T> left, RenderInstanceProperty<T> right) =>
        left.Equals(right);

    public static bool operator !=(RenderInstanceProperty<T> left, RenderInstanceProperty<T> right) =>
        !left.Equals(right);
}

/// <summary>A strongly typed property handle resolved against one immutable metadata layout.</summary>
public readonly struct ResolvedRenderInstanceProperty<T> : IEquatable<ResolvedRenderInstanceProperty<T>>
    where T : unmanaged
{
    private readonly RenderInstancePropertyLayout? _layout;
    private readonly RenderInstancePropertyDescriptor? _descriptor;

    internal ResolvedRenderInstanceProperty(
        RenderInstancePropertyLayout layout,
        RenderInstancePropertyDescriptor descriptor)
    {
        _layout = layout;
        _descriptor = descriptor;
    }

    public bool IsValid => _layout is not null && _descriptor is not null;

    public RenderInstancePropertyLayout Layout =>
        _layout ?? throw new InvalidOperationException("The resolved render-instance property is uninitialized.");

    public RenderInstancePropertyDescriptor Descriptor =>
        _descriptor ?? throw new InvalidOperationException("The resolved render-instance property is uninitialized.");

    public int Ordinal => Descriptor.Ordinal;

    public RenderInstancePropertyKey Key => Descriptor.Key;

    public RenderInstancePropertyEncoding Encoding => Descriptor.Encoding;

    internal bool BelongsTo(RenderInstancePropertyLayout layout) => ReferenceEquals(_layout, layout);

    public bool Equals(ResolvedRenderInstanceProperty<T> other) =>
        ReferenceEquals(_layout, other._layout) && ReferenceEquals(_descriptor, other._descriptor);

    public override bool Equals(object? obj) =>
        obj is ResolvedRenderInstanceProperty<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_layout, _descriptor);

    public static bool operator ==(
        ResolvedRenderInstanceProperty<T> left,
        ResolvedRenderInstanceProperty<T> right) => left.Equals(right);

    public static bool operator !=(
        ResolvedRenderInstanceProperty<T> left,
        ResolvedRenderInstanceProperty<T> right) => !left.Equals(right);
}

/// <summary>An immutable opaque property entry in one composed metadata contract.</summary>
public sealed class RenderInstancePropertyDescriptor
{
    private readonly string[] _contributors;

    internal RenderInstancePropertyDescriptor(
        RenderInstancePropertyRegistration registration,
        int ordinal,
        int metadataWordOffset)
    {
        RenderInstancePropertyDeclaration declaration = registration.Declaration;
        Ordinal = ordinal;
        MetadataWordOffset = metadataWordOffset;
        Key = declaration.Key;
        Encoding = declaration.Encoding;
        _contributors = new string[registration.Contributors.Count];
        registration.Contributors.CopyTo(_contributors);
        Array.Sort(_contributors, StringComparer.Ordinal);
    }

    public int Ordinal { get; }

    public int MetadataWordOffset { get; }

    public RenderInstancePropertyKey Key { get; }

    public RenderInstancePropertyEncoding Encoding { get; }

    public IReadOnlyList<string> Contributors => _contributors;

    internal bool HasSameContract(RenderInstancePropertyDeclaration declaration) =>
        Key == declaration.Key && Encoding == declaration.Encoding;

    internal RenderInstancePropertyDeclaration CloneDeclaration() => new(Key, Encoding);
}

internal sealed class RenderInstancePropertyRegistration
{
    internal RenderInstancePropertyRegistration(
        RenderInstancePropertyDeclaration declaration,
        string contributor)
    {
        Declaration = declaration;
        Contributors = new SortedSet<string>(StringComparer.Ordinal) { contributor };
    }

    internal RenderInstancePropertyDeclaration Declaration { get; }

    internal SortedSet<string> Contributors { get; }
}

internal sealed class RenderInstancePropertyDeclaration
{
    internal RenderInstancePropertyDeclaration(
        RenderInstancePropertyKey key,
        RenderInstancePropertyEncoding encoding)
    {
        Key = key;
        Encoding = encoding;
    }

    internal RenderInstancePropertyKey Key { get; }

    internal RenderInstancePropertyEncoding Encoding { get; }

    internal bool HasSameContract(RenderInstancePropertyDeclaration other) =>
        Key == other.Key && Encoding == other.Encoding;
}

internal static class RenderInstancePropertyValue<T>
    where T : unmanaged
{
    internal static int Size => Unsafe.SizeOf<T>();

    internal static void Write(Span<byte> destination, in T value)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"A {typeof(T).Name} property requires {Size} bytes.", nameof(destination));
        MemoryMarshal.Write(destination, in value);
    }

    internal static T Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"A {typeof(T).Name} property requires {Size} bytes.", nameof(source));
        return MemoryMarshal.Read<T>(source);
    }
}
