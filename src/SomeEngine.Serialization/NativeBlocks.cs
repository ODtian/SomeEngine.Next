using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Serialization;

/// <summary>
/// Generator-produced proof that one unmanaged, padding-free sequential layout is valid for the
/// current ABI. Callers consume this value; they do not invent layout fingerprints at runtime.
/// </summary>
public readonly record struct NativeLayoutProof<T>
    where T : unmanaged
{
    private NativeLayoutProof(
        ulong fingerprint,
        int size,
        int coveredFieldBytes,
        int alignment,
        uint architectureToken,
        byte pointerSize,
        ulong abiTokenHash)
    {
        Fingerprint = fingerprint;
        Size = size;
        CoveredFieldBytes = coveredFieldBytes;
        Alignment = alignment;
        ArchitectureToken = architectureToken;
        PointerSize = pointerSize;
        AbiTokenHash = abiTokenHash;
    }

    public ulong Fingerprint { get; }
    public int Size { get; }
    public int CoveredFieldBytes { get; }
    public int Alignment { get; }
    public uint ArchitectureToken { get; }
    public byte PointerSize { get; }
    public ulong AbiTokenHash { get; }

    /// <summary>Compiler hook used by generated contract code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static NativeLayoutProof<T> CreateGenerated(
        ulong generatedLayoutFingerprint,
        int generatedSize,
        int coveredFieldBytes,
        int requiredAlignment,
        string abiToken)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Native raw blocks require a little-endian host.");
        if (generatedLayoutFingerprint == 0)
            throw new ArgumentOutOfRangeException(nameof(generatedLayoutFingerprint));
        if (generatedSize != Unsafe.SizeOf<T>())
        {
            throw new PlatformNotSupportedException(
                $"Generated native size {generatedSize} does not match runtime size {Unsafe.SizeOf<T>()} " +
                $"for '{typeof(T).FullName}'.");
        }
        if (coveredFieldBytes != generatedSize)
        {
            throw new PlatformNotSupportedException(
                $"Native layout '{typeof(T).FullName}' covers {coveredFieldBytes} field bytes in a " +
                $"{generatedSize}-byte value. Layouts with ABI padding cannot use NativeRaw.");
        }
        if (requiredAlignment <= 0 || requiredAlignment > 64
            || (requiredAlignment & (requiredAlignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredAlignment),
                "Native alignment must be a power of two between 1 and 64 bytes.");
        }
        if ((generatedSize & (requiredAlignment - 1)) != 0)
        {
            throw new PlatformNotSupportedException(
                $"Native size {generatedSize} is not a multiple of required alignment {requiredAlignment}.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(abiToken);

        return new NativeLayoutProof<T>(
            generatedLayoutFingerprint,
            generatedSize,
            coveredFieldBytes,
            requiredAlignment,
            CurrentArchitectureToken(),
            checked((byte)IntPtr.Size),
            HashAbiToken(abiToken));
    }

    internal void ThrowIfUnsupported()
    {
        if (!BitConverter.IsLittleEndian
            || Size != Unsafe.SizeOf<T>()
            || CoveredFieldBytes != Size
            || ArchitectureToken != CurrentArchitectureToken()
            || PointerSize != IntPtr.Size)
        {
            throw new PlatformNotSupportedException(
                $"Native layout proof for '{typeof(T).FullName}' does not match the current runtime ABI.");
        }
    }

    private static uint CurrentArchitectureToken()
    {
        // Architecture is a closed runtime enum. Its numeric value avoids formatting/hashing a
        // string on every raw-view access and makes steady-state proof validation allocation-free.
        return checked((uint)RuntimeInformation.ProcessArchitecture + 1U);
    }

    private static ulong HashAbiToken(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        int byteCount = Encoding.UTF8.GetByteCount(token);
        byte[]? rented = null;
        Span<byte> stack = stackalloc byte[128];
        Span<byte> bytes = byteCount <= stack.Length
            ? stack
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int written = Encoding.UTF8.GetBytes(token, bytes);
            SHA256.HashData(bytes[..written], hash);
            return BinaryPrimitives.ReadUInt64LittleEndian(hash);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

public static class NativeBlock
{
    private const byte FormatVersion = 2;
    private const int FixedHeaderSize = 48;

    public static bool IsSupported<T>(in NativeLayoutProof<T> proof)
        where T : unmanaged
    {
        try
        {
            proof.ThrowIfUnsupported();
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static void Write<T>(
        ref BinaryDataWriter writer,
        ReadOnlySpan<T> values,
        in NativeLayoutProof<T> proof)
        where T : unmanaged
    {
        proof.ThrowIfUnsupported();

        int blockStart = writer.WrittenCount;
        int payloadOffset = checked(Align(blockStart + FixedHeaderSize, proof.Alignment) - blockStart);
        writer.WriteUInt64(proof.Fingerprint);
        writer.WriteInt32(proof.Size);
        writer.WriteInt32(values.Length);
        writer.WriteInt32(proof.Alignment);
        writer.WriteUInt32(proof.ArchitectureToken);
        writer.WriteByte(proof.PointerSize);
        writer.WriteByte(BitConverter.IsLittleEndian ? (byte)1 : (byte)0);
        writer.WriteByte(FormatVersion);
        writer.WriteByte(0);
        writer.WriteUInt64(proof.AbiTokenHash);
        writer.WriteInt32(payloadOffset);
        writer.WriteInt32(proof.CoveredFieldBytes);
        writer.WriteInt32(0);
        writer.WriteZeroes(payloadOffset - FixedHeaderSize);
        writer.WriteBytes(MemoryMarshal.AsBytes(values));
    }

    /// <summary>
    /// Reads a proven native view from a normal bounded reader. The returned span borrows the
    /// reader's backing memory and remains valid only for that owner's lifetime.
    /// </summary>
    public static ReadOnlySpan<T> Read<T>(
        ref BinaryDataReader reader,
        in NativeLayoutProof<T> proof)
        where T : unmanaged
    {
        proof.ThrowIfUnsupported();
        int blockStart = reader.Position;

        ulong fingerprint = reader.ReadUInt64();
        int elementSize = reader.ReadInt32();
        int count = reader.ReadCollectionCount(
            "native block",
            elementAllocationBytes: 0,
            fixedAllocationBytes: 0);
        int alignment = reader.ReadInt32();
        uint architecture = reader.ReadUInt32();
        byte pointerSize = reader.ReadByte();
        byte littleEndian = reader.ReadByte();
        byte version = reader.ReadByte();
        byte reserved = reader.ReadByte();
        ulong abiTokenHash = reader.ReadUInt64();
        int payloadOffset = reader.ReadInt32();
        int coveredFieldBytes = reader.ReadInt32();
        int reservedTail = reader.ReadInt32();

        ValidateMetadata(
            fingerprint,
            elementSize,
            alignment,
            architecture,
            pointerSize,
            littleEndian,
            version,
            reserved,
            abiTokenHash,
            payloadOffset,
            coveredFieldBytes,
            reservedTail,
            blockStart,
            proof);
        ReadOnlySpan<byte> padding = reader.ReadBytes(payloadOffset - FixedHeaderSize);
        EnsureZeroPadding(padding);
        int byteLength = CheckedByteLength(count, elementSize);
        ReadOnlySpan<byte> bytes = reader.ReadBytes(byteLength);
        EnsureAddressAligned<T>(bytes, proof.Alignment);
        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    /// <summary>
    /// Allocation-free bounded raw-view parser for already resident bytes. Error paths may allocate
    /// exceptions; a successful steady-state call allocates no managed memory.
    /// </summary>
    public static ReadOnlySpan<T> Read<T>(
        ReadOnlySpan<byte> source,
        in NativeLayoutProof<T> proof,
        int maxElementCount,
        out int consumedBytes)
        where T : unmanaged
    {
        proof.ThrowIfUnsupported();
        ArgumentOutOfRangeException.ThrowIfNegative(maxElementCount);
        if (source.Length < FixedHeaderSize)
            throw new InvalidDataException("Native block header is truncated.");

        ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(source);
        int elementSize = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        if (count < 0 || count > maxElementCount)
        {
            throw new InvalidDataException(
                $"Native block element count {count} exceeds configured limit {maxElementCount}.");
        }
        int alignment = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
        uint architecture = BinaryPrimitives.ReadUInt32LittleEndian(source[20..]);
        byte pointerSize = source[24];
        byte littleEndian = source[25];
        byte version = source[26];
        byte reserved = source[27];
        ulong abiTokenHash = BinaryPrimitives.ReadUInt64LittleEndian(source[28..]);
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(source[36..]);
        int coveredFieldBytes = BinaryPrimitives.ReadInt32LittleEndian(source[40..]);
        int reservedTail = BinaryPrimitives.ReadInt32LittleEndian(source[44..]);

        ValidateMetadata(
            fingerprint,
            elementSize,
            alignment,
            architecture,
            pointerSize,
            littleEndian,
            version,
            reserved,
            abiTokenHash,
            payloadOffset,
            coveredFieldBytes,
            reservedTail,
            blockStart: 0,
            proof);
        if (payloadOffset > source.Length)
            throw new InvalidDataException("Native block padding is truncated.");
        EnsureZeroPadding(source[FixedHeaderSize..payloadOffset]);
        int byteLength = CheckedByteLength(count, elementSize);
        int end;
        try
        {
            end = checked(payloadOffset + byteLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Native block range overflowed.", exception);
        }
        if (end > source.Length)
            throw new InvalidDataException("Native block payload is truncated.");

        ReadOnlySpan<byte> bytes = source.Slice(payloadOffset, byteLength);
        EnsureAddressAligned<T>(bytes, proof.Alignment);
        consumedBytes = end;
        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    private static void ValidateMetadata<T>(
        ulong fingerprint,
        int elementSize,
        int alignment,
        uint architecture,
        byte pointerSize,
        byte littleEndian,
        byte version,
        byte reserved,
        ulong abiTokenHash,
        int payloadOffset,
        int coveredFieldBytes,
        int reservedTail,
        int blockStart,
        in NativeLayoutProof<T> proof)
        where T : unmanaged
    {
        if (fingerprint != proof.Fingerprint)
        {
            throw new InvalidDataException(
                $"Native layout fingerprint 0x{fingerprint:X16} does not match expected " +
                $"0x{proof.Fingerprint:X16} for '{typeof(T).FullName}'.");
        }
        if (elementSize != proof.Size || elementSize != Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException(
                $"Native element size {elementSize} does not match proven runtime size {proof.Size} " +
                $"for '{typeof(T).FullName}'.");
        }
        if (alignment != proof.Alignment
            || architecture != proof.ArchitectureToken
            || pointerSize != proof.PointerSize
            || littleEndian != 1
            || version != FormatVersion
            || reserved != 0
            || abiTokenHash != proof.AbiTokenHash
            || coveredFieldBytes != proof.CoveredFieldBytes
            || reservedTail != 0)
        {
            throw new InvalidDataException(
                $"Native block ABI metadata does not match the proven layout for '{typeof(T).FullName}'.");
        }
        if (payloadOffset < FixedHeaderSize
            || payloadOffset - FixedHeaderSize >= proof.Alignment
            || ((blockStart + payloadOffset) & (proof.Alignment - 1)) != 0)
        {
            throw new InvalidDataException(
                $"Native block payload offset {payloadOffset} does not satisfy its proven " +
                $"{proof.Alignment}-byte wire alignment.");
        }
    }

    private static int CheckedByteLength(int count, int elementSize)
    {
        try
        {
            return checked(count * elementSize);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Native block byte length overflowed.", exception);
        }
    }

    private static void EnsureZeroPadding(ReadOnlySpan<byte> padding)
    {
        foreach (byte value in padding)
        {
            if (value != 0)
                throw new InvalidDataException("Native block alignment padding is non-zero.");
        }
    }

    private static unsafe void EnsureAddressAligned<T>(ReadOnlySpan<byte> bytes, int alignment)
        where T : unmanaged
    {
        if (bytes.IsEmpty || alignment == 1)
            return;
        fixed (byte* pointer = bytes)
        {
            if (((nuint)pointer & checked((nuint)(alignment - 1))) != 0)
            {
                throw new InvalidDataException(
                    $"Native block data address is not aligned to the proven {alignment}-byte boundary " +
                    $"for '{typeof(T).FullName}'. Use an aligned range owner or the canonical fallback.");
            }
        }
    }

    private static int Align(int value, int alignment)
    {
        int mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }
}
