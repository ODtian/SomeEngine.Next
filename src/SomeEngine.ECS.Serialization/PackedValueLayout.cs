using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

internal static class PackedValueLayout
{
    private const string ProofDomain = "SomeEngine.ECS.PackedValueLayout.v1";

    internal static bool IsVerified<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T>(
        int expectedSize,
        ulong expectedFingerprint)
        where T : struct
    {
        if (expectedSize < 0 ||
            expectedFingerprint == 0 ||
            !BitConverter.IsLittleEndian ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
            Unsafe.SizeOf<T>() != expectedSize)
        {
            return false;
        }

        Type type = typeof(T);
        StructLayoutAttribute? layout = type.StructLayoutAttribute;
        if (layout is null ||
            layout.Value != LayoutKind.Sequential ||
            layout.Pack != 1)
        {
            return false;
        }

        FieldInfo[] fields = type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        var fieldLayouts = new List<FieldLayout>(fields.Length);
        int nextOffset = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (!TryGetFixedWidthPrimitive(field.FieldType, out uint kind, out int size))
                return false;

            // Modern reflection returns declared fields in metadata order. Sequential Pack=1
            // layout uses that same order and cannot introduce alignment padding, so the actual
            // offset is the accumulated fixed-width field size. Avoid Marshal.OffsetOf here:
            // NativeAOT intentionally omits structure-marshalling data for types that are not
            // used by an interop signature, even though their managed layout is fully known.
            if (nextOffset > int.MaxValue - size)
                return false;
            fieldLayouts.Add(new FieldLayout(nextOffset, kind, size));
            nextOffset += size;
        }

        return nextOffset == expectedSize &&
               ComputeFingerprint(
                   expectedSize,
                   CollectionsMarshal.AsSpan(fieldLayouts)) == expectedFingerprint;
    }

    private static bool TryGetFixedWidthPrimitive(Type type, out uint kind, out int size)
    {
        if (type == typeof(byte))
            return Shape(1, 1, out kind, out size);
        if (type == typeof(sbyte))
            return Shape(2, 1, out kind, out size);
        if (type == typeof(short))
            return Shape(3, 2, out kind, out size);
        if (type == typeof(ushort))
            return Shape(4, 2, out kind, out size);
        if (type == typeof(char))
            return Shape(5, 2, out kind, out size);
        if (type == typeof(int))
            return Shape(6, 4, out kind, out size);
        if (type == typeof(uint))
            return Shape(7, 4, out kind, out size);
        if (type == typeof(float))
            return Shape(8, 4, out kind, out size);
        if (type == typeof(long))
            return Shape(9, 8, out kind, out size);
        if (type == typeof(ulong))
            return Shape(10, 8, out kind, out size);
        if (type == typeof(double))
            return Shape(11, 8, out kind, out size);

        kind = 0;
        size = 0;
        return false;
    }

    private static bool Shape(uint valueKind, int valueSize, out uint kind, out int size)
    {
        kind = valueKind;
        size = valueSize;
        return true;
    }

    private static ulong ComputeFingerprint(
        int size,
        ReadOnlySpan<FieldLayout> fields)
    {
        ulong hash = BinaryFieldKey.FromName(ProofDomain);
        AddUInt32(ref hash, unchecked((uint)size));
        AddUInt32(ref hash, unchecked((uint)fields.Length));
        for (int i = 0; i < fields.Length; i++)
        {
            FieldLayout field = fields[i];
            AddUInt32(ref hash, field.Kind);
            AddUInt32(ref hash, unchecked((uint)field.Offset));
            AddUInt32(ref hash, unchecked((uint)field.Size));
        }

        return hash == 0 ? 1ul : hash;
    }

    private static void AddUInt32(ref ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * 1099511628211ul;
    }

    private static void AddBytes(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * 1099511628211ul;
    }

    private readonly struct FieldLayout
    {
        internal FieldLayout(int offset, uint kind, int size)
        {
            Offset = offset;
            Kind = kind;
            Size = size;
        }

        internal int Offset { get; }

        internal uint Kind { get; }

        internal int Size { get; }
    }
}
