using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class TextSerializationTests
{
    [Fact]
    public void DerivedFieldsRemainBinaryAndCannotBeEditedThroughText()
    {
        var value = new TextWithDerived { Name = "source", Compiled = new BinaryOnlyValue { Number = 37 } };
        string text = TextSerializer.Serialize(value);
        Assert.DoesNotContain("Compiled", text);
        var decoded = TextSerializer.Deserialize<TextWithDerived>(text);
        Assert.Equal("source", decoded.Name);
        Assert.Equal(0, decoded.Compiled.Number);
        var bytes = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(bytes, value);
        Assert.Equal(37, BinarySerializer.Deserialize<TextWithDerived>(bytes.WrittenSpan).Compiled.Number);
        Assert.Throws<JsonException>(() => TextSerializer.Deserialize<TextWithDerived>("{\"Compiled\":{\"Number\":1}}"));
    }
    [Fact]
    public void DictionaryOrderAndUnionTagsHaveTheSameMeaningInBothEncodings()
    {
        var first = new TextChoices { Choices = new Dictionary<string, ITextChoice?>
            { ["z"] = null, ["a"] = new TextNumberChoice { Number = 17 } } };
        var second = new TextChoices { Choices = new Dictionary<string, ITextChoice?>
            { ["a"] = new TextNumberChoice { Number = 17 }, ["z"] = null } };
        string text = TextSerializer.Serialize(first);
        Assert.Equal(text, TextSerializer.Serialize(second));
        var decoded = TextSerializer.Deserialize<TextChoices>(text);
        Assert.Equal(17, Assert.IsType<TextNumberChoice>(decoded.Choices["a"]).Number);
        Assert.Null(decoded.Choices["z"]);
        var before = new ArrayBufferWriter<byte>();
        var after = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(before, first);
        BinarySerializer.Write(after, decoded);
        Assert.Equal(before.WrittenSpan.ToArray(), after.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData("{\"Choices\":[{\"Key\":\"a\",\"Value\":null},{\"Key\":\"a\",\"Value\":null}]}")]
    [InlineData("{\"Choices\":[{\"Key\":\"a\",\"Value\":{\"Tag\":99,\"Value\":{}}}]}")]
    [InlineData("{\"Choices\":[{\"Key\":\"a\",\"Key\":\"b\",\"Value\":null}]}")]
    [InlineData("{\"Choices\":[{\"Key\":\"a\"}]}")]
    public void DictionaryAndUnionSourcesRejectAmbiguousContent(string text)
        => Assert.Throws<JsonException>(() => TextSerializer.Deserialize<TextChoices>(text));

    [Fact]
    public void TextAndBinaryUseTheSameFieldsAndPreserveValues()
    {
        var source = new TextValues
        {
            Name = "曲线", Number = 0.1f, Mode = TextMode.Linear,
            Points = [new(1.5f, -4)], Optional = [], Bytes = new byte[] { 1, 5, 9 },
        };
        string json = TextSerializer.Serialize(source);
        Assert.Contains("\"Number\": 0.1", json);
        var fromText = TextSerializer.Deserialize<TextValues>(json);
        var first = new ArrayBufferWriter<byte>();
        var second = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(first, source);
        BinarySerializer.Write(second, fromText);
        Assert.Equal(first.WrittenSpan.ToArray(), second.WrittenSpan.ToArray());
        Assert.Empty(fromText.Optional!.Value);
    }

    [Fact]
    public void OmittedFieldsKeepDefaultsAndExplicitNullClearsNullableValues()
    {
        var value = TextSerializer.Deserialize<TextValues>("""{/* editable */ "Optional": null,} """);
        Assert.Equal("default", value.Name);
        Assert.Equal(7, value.Number);
        Assert.Null(value.Optional);
        Assert.Empty(value.Points);
    }

    [Fact]
    public void ApplyingTextReusesWritableCollectionsAndPreservesOmittedFields()
    {
        var points = new List<TextPoint> { new(2, 3) };
        var target = new TextValues { Points = points, Name = "retained" };
        TextSerializer.DeserializeInto(Encoding.UTF8.GetBytes("""{"Points":[{"X":4,"Y":8}]}"""), target);
        Assert.Same(points, target.Points);
        Assert.Equal(new TextPoint(4, 8), Assert.Single(points));
        Assert.Equal("retained", target.Name);
    }

    [Theory]
    [InlineData("{\"Name\":null}")]
    [InlineData("{\"Name\":\"a\",\"Name\":\"b\"}")]
    [InlineData("{\"Typo\":0}")]
    public void InvalidFieldsAreRejected(string text)
        => Assert.Throws<JsonException>(() => TextSerializer.Deserialize<TextValues>(text));

    [Fact]
    public void RequiredAndInitOnlyFieldsAreReadWithoutDiscardingDefaults()
    {
        var value = TextSerializer.Deserialize<TextRequired>("""{"Name":"required"}""");
        Assert.Equal("required", value.Name);
        Assert.Equal(13, value.Count);
        Assert.Throws<JsonException>(() => TextSerializer.Deserialize<TextRequired>("{}"));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("zh-CN")]
    public void NumberEncodingIsIndependentOfCulture(string culture)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var value = TextSerializer.Deserialize<TextValues>("""{"Number":1.25}""");
            Assert.Equal(1.25f, value.Number);
            Assert.Contains("\"Number\": 1.25", TextSerializer.Serialize(value));
            value.Number = float.PositiveInfinity;
            Assert.Equal(value.Number, TextSerializer.Deserialize<TextValues>(TextSerializer.Serialize(value)).Number);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}

public enum TextMode : byte { Constant, Linear }

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(X), nameof(Y))]
public readonly partial record struct TextPoint(float X, float Y);

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Name), nameof(Number), nameof(Mode), nameof(Points), nameof(Optional), nameof(Bytes))]
public sealed partial class TextValues
{
    public string Name { get; set; } = "default";
    public float Number { get; set; } = 7;
    public TextMode? Mode { get; set; }
    public IList<TextPoint> Points { get; set; } = new List<TextPoint>();
    public ImmutableArray<int>? Optional { get; set; } = [1, 2];
    public ReadOnlyMemory<byte>? Bytes { get; set; }
}

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Name), nameof(Count))]
public sealed partial class TextRequired
{
    public required string Name { get; init; }
    public int Count { get; init; } = 13;
}

[BinaryUnion(typeof(TextNumberChoice))]
public interface ITextChoice;

[BinarySerializable, TextSerializable, BinaryUnionCase(1)]
[BinaryFields(nameof(Number))]
public sealed partial class TextNumberChoice : ITextChoice
{
    public int Number { get; set; }
}

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Choices))]
public sealed partial class TextChoices
{
    public IDictionary<string, ITextChoice?> Choices { get; set; } = new Dictionary<string, ITextChoice?>();
}

[BinarySerializable]
[BinaryFields(nameof(Number))]
public sealed partial class BinaryOnlyValue
{
    public int Number { get; set; }
}

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Name), nameof(Compiled))]
public sealed partial class TextWithDerived
{
    public string Name { get; set; } = "";
    [TextIgnore] public BinaryOnlyValue Compiled { get; set; } = new();
}
