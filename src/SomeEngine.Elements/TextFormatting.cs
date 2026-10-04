using System.Buffers;
using System.Runtime.CompilerServices;

namespace SomeEngine.Elements;

internal interface IFormattedText
{
    IFormatProvider? Provider { get; }
    string Format(string? previous, IFormatProvider? provider);
}

internal static class TextFormatting
{
    internal static bool ValuesEqual<T>(T first, T second) where T : struct
    {
        if (typeof(T) == typeof(decimal))
        {
            Span<int> firstBits = stackalloc int[4];
            Span<int> secondBits = stackalloc int[4];
            decimal.GetBits(Unsafe.As<T, decimal>(ref first), firstBits);
            decimal.GetBits(Unsafe.As<T, decimal>(ref second), secondBits);
            return firstBits.SequenceEqual(secondBits);
        }
        if (typeof(T) == typeof(DateTime))
            return Unsafe.As<T, DateTime>(ref first).ToBinary() == Unsafe.As<T, DateTime>(ref second).ToBinary();
        if (typeof(T) == typeof(DateTimeOffset))
        {
            DateTimeOffset firstDate = Unsafe.As<T, DateTimeOffset>(ref first);
            DateTimeOffset secondDate = Unsafe.As<T, DateTimeOffset>(ref second);
            return firstDate.Ticks == secondDate.Ticks && firstDate.Offset == secondDate.Offset;
        }
        if (typeof(T) == typeof(double))
            return BitConverter.DoubleToInt64Bits(Unsafe.As<T, double>(ref first)) ==
                BitConverter.DoubleToInt64Bits(Unsafe.As<T, double>(ref second));
        if (typeof(T) == typeof(float))
            return BitConverter.SingleToInt32Bits(Unsafe.As<T, float>(ref first)) ==
                BitConverter.SingleToInt32Bits(Unsafe.As<T, float>(ref second));
        if (typeof(T) == typeof(Half))
            return BitConverter.HalfToInt16Bits(Unsafe.As<T, Half>(ref first)) ==
                BitConverter.HalfToInt16Bits(Unsafe.As<T, Half>(ref second));
        return EqualityComparer<T>.Default.Equals(first, second);
    }

    internal static string Format<T>(T value, string? previous, ReadOnlySpan<char> format, IFormatProvider? provider)
        where T : struct, ISpanFormattable
    {
        Span<char> characters = stackalloc char[256];
        if (value.TryFormat(characters, out int written, format, provider))
            return GetText(characters[..written], previous);

        int capacity = 512;
        while (true)
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                if (value.TryFormat(buffer, out written, format, provider))
                    return GetText(buffer.AsSpan(0, written), previous);
                capacity = checked(buffer.Length * 2);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    private static string GetText(ReadOnlySpan<char> characters, string? previous) =>
        previous is not null && characters.SequenceEqual(previous) ? previous : new string(characters);
}
