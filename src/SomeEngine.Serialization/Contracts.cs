using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Serialization;

/// <summary>The only supported wire compatibility policy: the complete schema must match.</summary>
public enum BinaryCompatibility : byte
{
    ExactSchema = 0,
}

/// <summary>Marks a partial C# type as the authoritative binary contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class BinaryContractAttribute : Attribute
{
    public BinaryContractAttribute(BinaryCompatibility compatibility = BinaryCompatibility.ExactSchema)
    {
        if (compatibility != BinaryCompatibility.ExactSchema)
            throw new ArgumentOutOfRangeException(nameof(compatibility), "Only ExactSchema contracts are supported.");
        Compatibility = compatibility;
    }

    public BinaryCompatibility Compatibility { get; }

    /// <summary>Current envelope epoch metadata. It never enables schema compatibility.</summary>
    public uint Epoch { get; init; } = 1;

    /// <summary>Stable logical type name. Defaults to the fully-qualified C# type name.</summary>
    public string? LogicalName { get; init; }
}

/// <summary>
/// Requests a compile-time proof that a binary-contract struct has a deterministic, padding-free
/// native layout suitable for <see cref="NativeBlock"/>. The struct must also declare
/// <see cref="System.Runtime.InteropServices.StructLayoutAttribute"/> with sequential layout and
/// an explicit, non-zero packing value.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class BinaryNativeLayoutAttribute : Attribute
{
    public BinaryNativeLayoutAttribute(string abiToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abiToken);
        AbiToken = abiToken;
    }

    /// <summary>
    /// Stable application-defined ABI identity. Change it whenever a native consumer's ABI changes.
    /// </summary>
    public string AbiToken { get; }
}

/// <summary>Overrides the stable logical field name used by schema fingerprints.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class BinaryNameAttribute : Attribute
{
    public BinaryNameAttribute(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        LogicalName = logicalName;
    }

    public string LogicalName { get; }
}

/// <summary>Excludes a member from a binary contract.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class BinaryIgnoreAttribute : Attribute;

/// <summary>
/// Marks an ignored authoring payload whose encoded bytes live in one semantic document chunk.
/// The named key and decoded-length members remain part of the exact root schema; source
/// generation emits one concrete <c>*Chunk</c> reference on both the contract and its view.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class BinaryChunkAttribute : Attribute
{
    public BinaryChunkAttribute(string keyMember, string decodedLengthMember)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyMember);
        ArgumentException.ThrowIfNullOrWhiteSpace(decodedLengthMember);
        KeyMember = keyMember;
        DecodedLengthMember = decodedLengthMember;
    }

    public string KeyMember { get; }
    public string DecodedLengthMember { get; }
}

/// <summary>
/// Declares the complete set of concrete binary-contract cases permitted for an interface or
/// abstract base-class member. The source generator uses each case's explicit
/// <see cref="BinaryUnionCaseAttribute"/> tag and emits closed, reflection-free dispatch. Cases
/// must be sealed classes assignable to the annotated type and marked with
/// <see cref="BinaryContractAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class BinaryUnionAttribute : Attribute
{
    public BinaryUnionAttribute(params Type[] cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Length == 0)
            throw new ArgumentException("A binary union must declare at least one case.", nameof(cases));
        Cases = cases;
    }

    public IReadOnlyList<Type> Cases { get; }
}

/// <summary>
/// Assigns a stable, non-zero wire tag to a concrete case listed by
/// <see cref="BinaryUnionAttribute"/>. Tags are explicit so adding a case cannot silently renumber
/// existing cases in the current exact schema.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BinaryUnionCaseAttribute : Attribute
{
    public BinaryUnionCaseAttribute(uint tag)
    {
        if (tag == 0)
            throw new ArgumentOutOfRangeException(nameof(tag), "Binary union tag zero is reserved.");
        Tag = tag;
    }

    public uint Tag { get; }
}

/// <summary>
/// Compile-time contract implemented by generated codecs. Runtime reflection and assembly scanning are
/// intentionally absent from the serialization path.
/// </summary>
public interface IBinaryContract<TSelf>
    where TSelf : IBinaryContract<TSelf>
{
    static abstract Guid TypeId { get; }

    static abstract ulong SchemaFingerprint { get; }

    static abstract BinaryCompatibility Compatibility { get; }

    static abstract uint SchemaEpoch { get; }

    static abstract void Write(ref BinaryDataWriter writer, TSelf value);

    static abstract TSelf Read(ref BinaryDataReader reader);
}

/// <summary>Untyped contract metadata used by catalogs and diagnostics.</summary>
public readonly record struct BinaryContractDescriptor(
    Guid TypeId,
    Type ContractType,
    ulong SchemaFingerprint,
    BinaryCompatibility Compatibility,
    uint SchemaEpoch);

public static class BinaryContract<T>
    where T : IBinaryContract<T>
{
    public static BinaryContractDescriptor Descriptor { get; } = new(
        T.TypeId,
        typeof(T),
        T.SchemaFingerprint,
        T.Compatibility,
        T.SchemaEpoch);

    public static bool IsCompatible(ulong encodedFingerprint, uint encodedEpoch)
        => T.Compatibility == BinaryCompatibility.ExactSchema
           && encodedFingerprint == T.SchemaFingerprint
           && encodedEpoch == T.SchemaEpoch;

    public static void ThrowIfIncompatible(ulong encodedFingerprint, uint encodedEpoch)
    {
        if (IsCompatible(encodedFingerprint, encodedEpoch))
            return;

        throw new BinarySchemaMismatchException(
            typeof(T),
            encodedFingerprint,
            T.SchemaFingerprint,
            encodedEpoch,
            T.SchemaEpoch,
            T.Compatibility);
    }
}

/// <summary>
/// Current-schema envelope metadata. It is inspectable without selecting or invoking a codec;
/// callers must still require exact type, fingerprint, compatibility, and epoch equality before
/// decoding.
/// </summary>
public readonly record struct BinaryEnvelopeMetadata(
    Guid TypeId,
    ulong SchemaFingerprint,
    BinaryCompatibility Compatibility,
    uint SchemaEpoch,
    long PayloadLength);

public static class BinaryTypeId
{
    public static Guid FromLogicalName(ReadOnlySpan<char> logicalName)
    {
        if (logicalName.IsEmpty)
            throw new ArgumentException("Logical name cannot be empty.", nameof(logicalName));

        int maximum = System.Text.Encoding.UTF8.GetMaxByteCount(logicalName.Length);
        byte[]? rented = null;
        Span<byte> stackBytes = stackalloc byte[256];
        Span<byte> bytes = maximum <= stackBytes.Length
            ? stackBytes
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maximum));
        try
        {
            int length = System.Text.Encoding.UTF8.GetBytes(logicalName, bytes);
            Span<byte> hash = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(bytes[..length], hash);
            return new Guid(hash[..16], bigEndian: true);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

public sealed class BinarySchemaMismatchException : IOException
{
    public BinarySchemaMismatchException(
        Type contractType,
        ulong actualFingerprint,
        ulong expectedFingerprint,
        uint actualEpoch,
        uint expectedEpoch,
        BinaryCompatibility compatibility)
        : base(
            $"Binary contract '{contractType.FullName}' is incompatible. " +
            $"Mode={compatibility}, encoded fingerprint=0x{actualFingerprint:X16}, " +
            $"reader fingerprint=0x{expectedFingerprint:X16}, encoded epoch={actualEpoch}, " +
            $"reader epoch={expectedEpoch}.")
    {
        ContractType = contractType;
        ActualFingerprint = actualFingerprint;
        ExpectedFingerprint = expectedFingerprint;
        ActualEpoch = actualEpoch;
        ExpectedEpoch = expectedEpoch;
        Compatibility = compatibility;
    }

    public Type ContractType { get; }
    public ulong ActualFingerprint { get; }
    public ulong ExpectedFingerprint { get; }
    public uint ActualEpoch { get; }
    public uint ExpectedEpoch { get; }
    public BinaryCompatibility Compatibility { get; }
}

public static class BinaryFieldKey
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Stable UTF-8 FNV-1a key used for semantic chunks and schema identities.</summary>
    public static ulong FromName(ReadOnlySpan<char> logicalName)
    {
        if (logicalName.IsEmpty)
            throw new ArgumentException("Logical name cannot be empty.", nameof(logicalName));

        Span<byte> stackBuffer = stackalloc byte[256];
        int maximum = System.Text.Encoding.UTF8.GetMaxByteCount(logicalName.Length);
        byte[]? rented = null;
        Span<byte> bytes = maximum <= stackBuffer.Length
            ? stackBuffer
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maximum));

        try
        {
            int length = System.Text.Encoding.UTF8.GetBytes(logicalName, bytes);
            ulong hash = OffsetBasis;
            for (int i = 0; i < length; i++)
            {
                hash ^= bytes[i];
                hash *= Prime;
            }

            return hash;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
