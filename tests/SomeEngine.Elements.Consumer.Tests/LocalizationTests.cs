using System.Globalization;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class LocalizationTests
{
    [Fact]
    public void PublicLocalizationChecksPass() => LocalizationChecks.Run();

    [Fact]
    public void ChangingLanguageUpdatesLocalizedTextAndPreservesOrdinaryText()
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            {
              "en-US": { "menu.start": "Start" },
              "zh-CN": { "menu.start": "开始" }
            }
            """));
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(320, 240) });
        frames.World.Add(canvas, new TextTheme(new TextStyle { Font = font }));
        Entity root = Views.Views.Spawn(frames.World, canvas, new Menu());
        frames.Frame();
        Entity[] texts = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("Start", frames.World.Read<TextContent>(texts[0]).Value);
        Assert.Equal("SomeEngine", frames.World.Read<TextContent>(texts[1]).Value);
        uint ordinaryTextVersion = frames.World.WriteVersion<TextContent>(texts[1]);
        uint ordinaryShapeVersion = frames.World.Read<ShapedText>(texts[1]).Version;
        uint localizedShapeVersion = frames.World.Read<ShapedText>(texts[0]).Version;

        frames.World.Replace(language, new Locale { Name = "zh-CN" });
        frames.Frame();

        Assert.Equal("开始", frames.World.Read<TextContent>(texts[0]).Value);
        Assert.Equal("SomeEngine", frames.World.Read<TextContent>(texts[1]).Value);
        Assert.Equal(ordinaryTextVersion, frames.World.WriteVersion<TextContent>(texts[1]));
        Assert.Equal(ordinaryShapeVersion, frames.World.Read<ShapedText>(texts[1]).Version);
        Assert.NotEqual(localizedShapeVersion, frames.World.Read<ShapedText>(texts[0]).Version);
        Assert.Equal(texts, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
    }

    private static Translations BuildTranslations(string source)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var content = new MemoryStream();
        Translations.Build(input, content);
        content.Position = 0;
        return Translations.Load(content);
    }

    [Theory]
    [InlineData(0, "0 items", "0 件")]
    [InlineData(1, "1 item", "1 件")]
    [InlineData(2, "2 items", "2 件")]
    [InlineData(-1, "-1 item", "-1 件")]
    [InlineData(1.5, "1.5 items", "1.5 件")]
    public void BuiltPluralRulesSelectEnglishAndChineseText(double count, string english, string chinese)
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            {
              "en-US": { "items": { "argument": 0, "one": "{0} item", "other": "{0} items" } },
              "zh-CN": { "items": { "argument": 0, "one": "错误分支", "other": "{0} 件" } }
            }
            """));
        Entity node = Views.Views.Spawn(frames.World, Entity.Null, new Text(Loc.Key("items", count)));

        frames.Frame();
        Assert.Equal(english, frames.World.Read<TextContent>(node).Value);
        frames.World.Replace(language, new Locale { Name = "zh-CN" });
        frames.Frame();
        Assert.Equal(chinese, frames.World.Read<TextContent>(node).Value);
    }

    [Fact]
    public void NumbersDatesAndTranslationArgumentsUseTheCurrentLanguage()
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            {
              "en-US": { "formatted": "{0:N2} / {1:MMMM}" },
              "de-DE": { "formatted": "{0:N2} / {1:MMMM}" }
            }
            """));
        Entity root = Views.Views.Spawn(frames.World, Entity.Null, new FormattedLabels(new DateTime(2026, 10, 4)));
        frames.Frame();
        Entity[] nodes = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal(["1,234.50 / October", "1,234.50", "October", "1234.50"],
            nodes.Select(node => frames.World.Read<TextContent>(node).Value));
        uint explicitCultureVersion = frames.World.WriteVersion<TextContent>(nodes[3]);

        frames.World.Replace(language, new Locale { Name = "de-DE" });
        frames.Frame();

        Assert.Equal(["1.234,50 / Oktober", "1.234,50", "Oktober", "1234.50"],
            nodes.Select(node => frames.World.Read<TextContent>(node).Value));
        Assert.Equal(explicitCultureVersion, frames.World.WriteVersion<TextContent>(nodes[3]));
        nodes[1].SetText(frames.World, 9876.5m, "N2");
        frames.Frame();
        Assert.Equal("9.876,50", frames.World.Read<TextContent>(nodes[1]).Value);
    }

    private readonly record struct FormattedLabels(DateTime Date) : IView, IComponent
    {
        public BuildTask Build()
        {
            Text(Loc.Key("formatted", 1234.5m, Date));
            Text(1234.5m, "N2");
            Text(Date, "MMMM");
            Text(1234.5m, "F2", CultureInfo.InvariantCulture);
            return default;
        }
    }

    [Fact]
    public void EqualKeysAndArgumentsPreserveTextParametersAndChangedArgumentsUpdateTheDisplay()
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            { "en-US": { "count": "Count: {0}" } }
            """));
        Entity root = Views.Views.Spawn(frames.World, Entity.Null, new CounterLabel(1));
        frames.Frame();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parameterVersion = frames.World.WriteVersion<Text>(node);
        uint contentVersion = frames.World.WriteVersion<TextContent>(node);

        frames.World.Replace(root, new CounterLabel(1));
        frames.Frame();

        Assert.Equal(parameterVersion, frames.World.WriteVersion<Text>(node));
        Assert.Equal(contentVersion, frames.World.WriteVersion<TextContent>(node));
        frames.World.Replace(root, new CounterLabel(2));
        frames.Frame();
        Assert.Equal(node, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal("Count: 2", frames.World.Read<TextContent>(node).Value);
        frames.World.Replace(language, BuildTranslations("""
            { "en-US": { "count": "Total: {0}" } }
            """));
        frames.Frame();
        Assert.Equal("Total: 2", frames.World.Read<TextContent>(node).Value);
    }

    private readonly record struct CounterLabel(int Count) : IView, IComponent
    {
        public BuildTask Build()
        {
            Text(Loc.Key("count", Count));
            return default;
        }
    }

    [Fact]
    public void WritingAnOrdinaryStringReplacesTheLocalizedTextIncludingAnEmptyString()
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            { "en-US": { "start": "Start" }, "zh-CN": { "start": "开始" } }
            """));
        Entity node = Views.Views.Spawn(frames.World, Entity.Null, new Text(Loc.Key("start")));
        frames.Frame();

        node.SetText(frames.World, "");
        frames.Frame();

        Assert.Equal("", frames.World.Read<TextContent>(node).Value);
        uint contentVersion = frames.World.WriteVersion<TextContent>(node);
        frames.World.Replace(language, new Locale { Name = "zh-CN" });
        frames.Frame();
        Assert.Equal("", frames.World.Read<TextContent>(node).Value);
        Assert.Equal(contentVersion, frames.World.WriteVersion<TextContent>(node));
    }

    [Fact]
    public void DecimalPrecisionChangesTheEnglishPluralAndDisplayedArgument()
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            { "en-US": { "items": { "argument": 0, "one": "{0} item", "other": "{0} items" } } }
            """));
        Entity root = Views.Views.Spawn(frames.World, Entity.Null, new DecimalLabel(1m));
        frames.Frame();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("1 item", frames.World.Read<TextContent>(node).Value);

        frames.World.Replace(root, new DecimalLabel(1.0m));
        frames.Frame();

        Assert.Equal("1.0 items", frames.World.Read<TextContent>(node).Value);
    }

    private readonly record struct DecimalLabel(decimal Count) : IView, IComponent
    {
        public BuildTask Build()
        {
            Text(Loc.Key("items", Count));
            return default;
        }
    }

    [Theory]
    [InlineData("date")]
    [InlineData("offset")]
    [InlineData("double")]
    [InlineData("float")]
    public void ChangedDateKindsOffsetsAndSignedZerosReachTheFormattedText(string example)
    {
        var date = new DateTime(2026, 10, 4);
        var offset = new DateTimeOffset(date, TimeSpan.Zero);
        (object first, object second, string key, string before, string after) = example switch
        {
            "date" => ((object)date, (object)DateTime.SpecifyKind(date, DateTimeKind.Utc), "date", "2026-10-04", "2026-10-04Z"),
            "offset" => ((object)offset, (object)offset.ToOffset(TimeSpan.FromHours(8)), "offset", "00:00 +00:00", "08:00 +08:00"),
            "double" => ((object)0d, (object)BitConverter.Int64BitsToDouble(long.MinValue), "number", "0", "-0"),
            _ => ((object)0f, (object)BitConverter.Int32BitsToSingle(int.MinValue), "number", "0", "-0"),
        };
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
        frames.World.Add(language, BuildTranslations("""
            { "en-US": { "date": "{0:yyyy-MM-ddK}", "offset": "{0:HH:mm zzz}", "number": "{0}" } }
            """));
        Entity root = Views.Views.Spawn(frames.World, Entity.Null, new ArgumentLabel(key, first));
        frames.Frame();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(before, frames.World.Read<TextContent>(node).Value);

        frames.World.Replace(root, new ArgumentLabel(key, second));
        frames.Frame();

        Assert.Equal(after, frames.World.Read<TextContent>(node).Value);
    }

    private readonly record struct ArgumentLabel(string Key, object Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Text(Loc.Key(Key, Value));
            return default;
        }
    }

#if DEBUG
    [Theory]
    [InlineData("locale", "Locale")]
    [InlineData("catalog", "Translations")]
    [InlineData("key", "key")]
    [InlineData("arguments", "format arguments")]
    [InlineData("duplicate", "one Locale")]
    public void LocalizationUsageErrorsProducePublicBuildErrors(string example, string message)
    {
        using var frames = new TextFrames();
        if (example != "locale")
        {
            Entity language = frames.World.CreateEntity(new Locale { Name = "en-US" });
            if (example != "catalog")
                frames.World.Add(language, BuildTranslations("""
                    { "en-US": { "greeting": "Hello, {0}" } }
                    """));
            if (example == "duplicate")
                frames.World.CreateEntity(new Locale { Name = "zh-CN" });
        }
        LocalizedText text = example switch
        {
            "key" => Loc.Key("missing"),
            "arguments" => Loc.Key("greeting"),
            _ => Loc.Key("greeting", "Ada"),
        };
        Entity node = Views.Views.Spawn(frames.World, Entity.Null, new Text(in text));

        frames.Frame();

        Assert.Contains(message, frames.World.ReadSparse<BuildError>(node).Exception.Message);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void PluralArgumentsRequireNumbersThroughDevelopmentChecks(string languageName)
    {
        using var frames = new TextFrames();
        Entity language = frames.World.CreateEntity(new Locale { Name = languageName });
        frames.World.Add(language, BuildTranslations("""
            {
              "en-US": { "items": { "argument": 0, "one": "{0} item", "other": "{0} items" } },
              "zh-CN": { "items": { "argument": 0, "other": "{0} 件" } }
            }
            """));
        Entity node = Views.Views.Spawn(frames.World, Entity.Null, new Text(Loc.Key("items", "many")));

        frames.Frame();

        Assert.Contains("numeric", frames.World.ReadSparse<BuildError>(node).Exception.Message);
    }
#endif

    private readonly record struct Menu : IView, IComponent
    {
        public BuildTask Build()
        {
            Text(Loc.Key("menu.start"));
            Text("SomeEngine");
            return default;
        }
    }
}
