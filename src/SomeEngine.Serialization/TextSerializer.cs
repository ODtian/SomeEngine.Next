using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SomeEngine.Serialization;

/// <summary>Requests static JSON encoding for the same fields used by binary serialization.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class TextSerializableAttribute : Attribute;

/// <summary>Excludes derived fields from editable text while retaining their binary encoding.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class TextIgnoreAttribute : Attribute;

public interface ITextSerializable<TSelf> where TSelf : ITextSerializable<TSelf>
{
    static abstract void WriteText(Utf8JsonWriter writer, TSelf value);
    static abstract TSelf ReadText(JsonElement value);
}

public interface ITextObjectReader<TSelf> where TSelf : class, ITextSerializable<TSelf>
{
    static abstract void ReadTextInto(JsonElement value, TSelf target);
}

/// <summary>Readable source encoding through generated or handwritten static methods.</summary>
public static class TextSerializer
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64,
    };

    public static void Write<T>(IBufferWriter<byte> destination, T value, bool indented = true)
        where T : ITextSerializable<T>
    {
        using var writer = new Utf8JsonWriter(destination, new() { Indented = indented });
        T.WriteText(writer, value);
        writer.Flush();
    }

    public static string Serialize<T>(T value, bool indented = true) where T : ITextSerializable<T>
    {
        var output = new ArrayBufferWriter<byte>();
        Write(output, value, indented);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    public static T Deserialize<T>(ReadOnlyMemory<byte> source) where T : ITextSerializable<T>
    {
        using var document = JsonDocument.Parse(source, ReadOptions);
        return T.ReadText(document.RootElement);
    }

    public static T Deserialize<T>(string source) where T : ITextSerializable<T>
    {
        using var document = JsonDocument.Parse(source, ReadOptions);
        return T.ReadText(document.RootElement);
    }

    /// <summary>Applies present fields; omitted fields keep their values. Failure can partially change the target.</summary>
    public static void DeserializeInto<T>(ReadOnlyMemory<byte> source, T target)
        where T : class, ITextSerializable<T>, ITextObjectReader<T>
    {
        ArgumentNullException.ThrowIfNull(target);
        using var document = JsonDocument.Parse(source, ReadOptions);
        T.ReadTextInto(document.RootElement, target);
    }

    public static char ReadCharacter(JsonElement value)
    {
        string? text = value.GetString();
        return text is { Length: 1 } ? text[0] : throw new JsonException("A character requires one UTF-16 code unit.");
    }

    /// <summary>Reads two named fields, in either order, rejecting omissions, duplicates and extra fields.</summary>
    public static (JsonElement First, JsonElement Second) ReadPair(JsonElement value, string firstName, string secondName)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        JsonElement first = default, second = default;
        foreach (var field in value.EnumerateObject())
        {
            if (field.NameEquals(firstName) && first.ValueKind == JsonValueKind.Undefined) first = field.Value;
            else if (field.NameEquals(secondName) && second.ValueKind == JsonValueKind.Undefined) second = field.Value;
            else throw new JsonException("Duplicate or unknown field: " + field.Name);
        }
        if (first.ValueKind == JsonValueKind.Undefined || second.ValueKind == JsonValueKind.Undefined)
            throw new JsonException("Missing pair fields.");
        return (first, second);
    }

    public static double ReadNumber(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() switch
        {
            "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity,
            _ => throw new JsonException("Unknown non-finite number."),
        } : value.GetDouble();

    public static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    public static void WriteNumber(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value)) writer.WriteNumberValue(value);
        else WriteNumber(writer, (double)value);
    }
}
