using System.Globalization;
using System.Text;
using System.Text.Json;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>A built translation directory stored beside the World's Locale.</summary>
public readonly struct Translations : IComponent
{
    private const uint FileTag = 0x534C4F43;
    private const int FileVersion = 1;
    private readonly Dictionary<string, LanguageTexts> _languages;

    private Translations(Dictionary<string, LanguageTexts> languages) => _languages = languages;

    /// <summary>Builds JSON language directories into content, keeping both streams open.</summary>
    public static void Build(Stream source, Stream destination)
    {
        using JsonDocument document = JsonDocument.Parse(source);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(FileTag);
        writer.Write(FileVersion);
        writer.Write(document.RootElement.EnumerateObject().Count());
        foreach (JsonProperty language in document.RootElement.EnumerateObject())
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(language.Name);
            PluralRule rule = culture.TwoLetterISOLanguageName switch
            {
                "en" => PluralRule.EnglishIntegerOne,
                "zh" => PluralRule.Other,
                _ => PluralRule.Unavailable,
            };
            writer.Write(culture.Name);
            writer.Write((byte)rule);
            writer.Write(language.Value.EnumerateObject().Count());
            foreach (JsonProperty text in language.Value.EnumerateObject())
            {
                writer.Write(text.Name);
                int argument = -1;
                string? one = null;
                string other;
                if (text.Value.ValueKind == JsonValueKind.String)
                {
                    other = text.Value.GetString()!;
                }
                else
                {
                    if (rule == PluralRule.Unavailable)
                        throw new InvalidDataException($"Plural content supports English and Chinese; '{culture.Name}' requires its rule table.");
                    argument = text.Value.GetProperty("argument").GetInt32();
                    if (argument < 0)
                        throw new InvalidDataException("A plural argument index must be nonnegative.");
                    other = text.Value.GetProperty("other").GetString()!;
                    if (text.Value.TryGetProperty("one", out JsonElement singular))
                        one = singular.GetString();
                }
                CompositeFormat.Parse(other);
                if (one is not null)
                    CompositeFormat.Parse(one);
                writer.Write(argument);
                writer.Write(other);
                writer.Write(one is not null);
                if (one is not null)
                    writer.Write(one);
            }
        }
    }

    /// <summary>Loads built translation content, keeping its stream open.</summary>
    public static Translations Load(Stream source)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != FileTag || reader.ReadInt32() != FileVersion)
            throw new InvalidDataException("The translation content header or version is invalid.");
        var languages = new Dictionary<string, LanguageTexts>(StringComparer.OrdinalIgnoreCase);
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            string name = reader.ReadString();
            var rule = (PluralRule)reader.ReadByte();
            var texts = new Dictionary<string, TranslationText>(StringComparer.Ordinal);
            int textCount = reader.ReadInt32();
            for (int textIndex = 0; textIndex < textCount; textIndex++)
            {
                string key = reader.ReadString();
                int argument = reader.ReadInt32();
                CompositeFormat other = CompositeFormat.Parse(reader.ReadString());
                CompositeFormat? one = reader.ReadBoolean() ? CompositeFormat.Parse(reader.ReadString()) : null;
                texts.Add(key, new TranslationText(argument, other, one));
            }
            languages.Add(name, new LanguageTexts(rule, texts));
        }
        return new Translations(languages);
    }

    internal string Resolve(string language, in LocalizedText text)
    {
        DevelopmentCheck.Require(!string.IsNullOrEmpty(text.Key), "Localized text requires a translation key.");
        DevelopmentCheck.Require(_languages is not null, "Localized text requires loaded Translations beside Locale.");
        bool hasLanguage = _languages.TryGetValue(language, out LanguageTexts texts);
        DevelopmentCheck.Require(hasLanguage, $"Translations require language '{language}'.");
        bool hasText = texts.Texts.TryGetValue(text.Key, out TranslationText value);
        DevelopmentCheck.Require(hasText, $"Translations require key '{text.Key}' in language '{language}'.");
        CompositeFormat format = value.Other;
        if (value.Argument >= 0)
        {
            DevelopmentCheck.Require(value.Argument < text.Arguments.Length,
                $"Translation '{text.Key}' requires plural argument {value.Argument}.");
            DevelopmentCheck.Require(text.Arguments.Span[value.Argument] is
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal,
                "Plural arguments require a numeric value.");
            if (texts.Rule == PluralRule.EnglishIntegerOne && IsIntegerOne(text.Arguments.Span[value.Argument]))
                format = value.One ?? value.Other;
        }
        DevelopmentCheck.Require(text.Arguments.Length >= format.MinimumArgumentCount,
            $"Translation '{text.Key}' requires {format.MinimumArgumentCount} format arguments.");
        return string.Format(CultureInfo.GetCultureInfo(language), format, text.Arguments.Span);
    }

    private static bool IsIntegerOne(object? value) => value switch
    {
        decimal number => (number == 1 || number == -1) && LocalizedText.CountFractionDigits(number) == 0,
        double number => number == 1 || number == -1,
        float number => number == 1 || number == -1,
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToDecimal(value, CultureInfo.InvariantCulture) is 1 or -1,
        _ => false,
    };

    private enum PluralRule : byte { Other, EnglishIntegerOne, Unavailable }
    private readonly record struct LanguageTexts(PluralRule Rule, Dictionary<string, TranslationText> Texts);
    private readonly record struct TranslationText(int Argument, CompositeFormat Other, CompositeFormat? One);
}
