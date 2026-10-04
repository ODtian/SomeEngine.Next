using System.Buffers;

namespace SomeEngine.Elements;

internal static class TextFormatting
{
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
