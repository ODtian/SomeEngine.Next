using System.Globalization;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public localization behavior shared with the NativeAOT executable.</summary>
internal static partial class LocalizationChecks
{
    internal static void Run()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "en-US": {
                "start": "Start",
                "items": { "argument": 0, "one": "{0} item", "other": "{0} items" },
                "formatted": "{0:N2} / {1:MMMM}"
              },
              "zh-CN": {
                "start": "开始",
                "items": { "argument": 0, "one": "错误分支", "other": "{0} 件" },
                "formatted": "{0:N2} / {1:MMMM}"
              },
              "de-DE": { "start": "Starten", "items": "{0} Dinge", "formatted": "{0:N2} / {1:MMMM}" }
            }
            """));
        using var content = new MemoryStream();
        Translations.Build(input, content);
        content.Position = 0;
        Translations translations = Translations.Load(content);
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity language = world.CreateEntity(new Locale { Name = "en-US" });
        world.Add(language, translations);
        Entity root = Views.Views.Spawn(world, Entity.Null, new Example(1));
        systems.Update();
        Entity[] nodes = Hierarchy<ElementTree>.GetChildren(world, root).ToArray();
        Require(world.Read<TextContent>(nodes[0]).Value == "Start", "The key resolves to English text.");
        Require(world.Read<TextContent>(nodes[1]).Value == "1 item", "English integer one selects the singular form.");
        Require(world.Read<TextContent>(nodes[2]).Value == "SomeEngine", "Ordinary text retains its string.");
        uint ordinaryVersion = world.WriteVersion<TextContent>(nodes[2]);
        uint explicitCultureVersion = world.WriteVersion<TextContent>(nodes[6]);
        world.Replace(language, new Locale { Name = "zh-CN" });
        systems.Update();
        Require(world.Read<TextContent>(nodes[0]).Value == "开始", "Language changes update the key's displayed text.");
        Require(world.Read<TextContent>(nodes[1]).Value == "1 件", "Chinese one selects the other form.");
        Require(world.WriteVersion<TextContent>(nodes[2]) == ordinaryVersion, "Ordinary text preserves its version.");
        world.Replace(root, new Example(2));
        systems.Update();
        Require(world.Read<TextContent>(nodes[1]).Value == "2 件", "Changed arguments update the displayed count.");
        world.Replace(language, new Locale { Name = "de-DE" });
        systems.Update();
        Require(world.Read<TextContent>(nodes[3]).Value == "1.234,50 / Oktober", "Translation arguments use the current language.");
        Require(world.Read<TextContent>(nodes[4]).Value == "1.234,50", "Number declarations use the current language.");
        Require(world.Read<TextContent>(nodes[5]).Value == "Oktober", "Date declarations use the current language.");
        Require(world.Read<TextContent>(nodes[6]).Value == "1234.50" &&
            world.WriteVersion<TextContent>(nodes[6]) == explicitCultureVersion, "An explicit culture controls its text.");
        for (int index = 0; index < 1000; index++)
            nodes[4].SetText(world, 1234.5m, "N2");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
            nodes[4].SetText(world, 1234.5m, "N2");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0 && world.Read<Text>(nodes[4]).Value == "1.234,50",
            "Number writes with a configured language retain zero allocations.");
        world.Replace(language, new Locale { Name = "en-US" });
        Entity fraction = Views.Views.Spawn(world, Entity.Null, new Text(Loc.Key("items", 1.0m)));
        systems.Update();
        Require(world.Read<TextContent>(fraction).Value == "1.0 items", "English visible decimal digits select the other form.");
        Require(input.CanRead && content.CanRead && content.CanWrite, "Content build and load keep caller streams open.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Example(int Count) : IView, IComponent
    {
        public BuildTask Build()
        {
            var date = new DateTime(2026, 10, 4);
            Text(Loc.Key("start"));
            Text(Loc.Key("items", Count));
            Text("SomeEngine");
            Text(Loc.Key("formatted", 1234.5m, date));
            Text(1234.5m, "N2");
            Text(date, "MMMM");
            Text(1234.5m, "F2", CultureInfo.InvariantCulture);
            return default;
        }
    }
}
