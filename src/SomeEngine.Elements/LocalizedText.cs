namespace SomeEngine.Elements;

/// <summary>A translation key and its positional format arguments.</summary>
public readonly record struct LocalizedText(string Key, ReadOnlyMemory<object?> Arguments)
{
    public bool Equals(LocalizedText other)
    {
        if (Key != other.Key || Arguments.Length != other.Arguments.Length)
            return false;
        ReadOnlySpan<object?> arguments = Arguments.Span;
        ReadOnlySpan<object?> otherArguments = other.Arguments.Span;
        for (int index = 0; index < arguments.Length; index++)
            if (!ArgumentsEqual(arguments[index], otherArguments[index]))
                return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key, StringComparer.Ordinal);
        foreach (object? argument in Arguments.Span)
        {
            hash.Add(argument);
            if (argument is decimal number)
                hash.Add(CountFractionDigits(number));
        }
        return hash.ToHashCode();
    }

    private static bool ArgumentsEqual(object? first, object? second)
    {
        if (!Equals(first, second))
            return false;
        return first switch
        {
            decimal number => CountFractionDigits(number) == CountFractionDigits((decimal)second!),
            DateTime date => date.Kind == ((DateTime)second!).Kind,
            DateTimeOffset date => date.Offset == ((DateTimeOffset)second!).Offset,
            double number => BitConverter.DoubleToInt64Bits(number) == BitConverter.DoubleToInt64Bits((double)second!),
            float number => BitConverter.SingleToInt32Bits(number) == BitConverter.SingleToInt32Bits((float)second!),
            _ => true,
        };
    }

    internal static int CountFractionDigits(decimal number)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(number, bits);
        return (bits[3] >> 16) & 0xFF;
    }
}

public static class Loc
{
    public static LocalizedText Key(string key, params object?[] arguments) => new(key, arguments);
}
