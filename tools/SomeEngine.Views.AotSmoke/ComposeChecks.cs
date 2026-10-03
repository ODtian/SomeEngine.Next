using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.AotSmoke;

internal static partial class ComposeChecks
{
    private static string s_decoration = " old";

    internal static void Run(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new Panel("first", 0));
        frames.Frame();
        Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, label).Single();
        Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, content).Single();
        Require(frames.World.ReadSparse<Label_View>(label).text == "first", "Compose stores the method parameters on its node.");
        Require(frames.World.ReadSparse<Card>(card).Title == "first old", "Compose declares its child content.");

        s_decoration = " new";
        frames.World.Replace(root, new Panel("first", 1));
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == label &&
            frames.World.ReadSparse<Card>(card).Title == "first old", "Equal Compose parameters preserve the existing content.");

        frames.World.Replace(root, new Panel("second", 2));
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Single() == label &&
            frames.World.ReadSparse<Card>(card).Title == "second new", "Changed Compose parameters update the same node's content.");
        CheckContent(frames);
    }

    private static void CheckContent(HeadlessFrameDriver frames)
    {
        s_decoration = " old";
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentPanel(2, 0));
        frames.Frame();
        Entity rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity[] content = Hierarchy<ElementTree>.GetChildren(frames.World, rows).ToArray();
        Entity[] cards = content.Select(child => Hierarchy<ElementTree>.GetChildren(frames.World, child).Single()).ToArray();
        Require(cards.Select(card => frames.World.ReadSparse<Card>(card).Title).SequenceEqual(["2 old", "5 old"]),
            "Content functions declare an independent child for each row.");

        s_decoration = " new";
        frames.World.Replace(root, new ContentPanel(2, 1));
        frames.Frame();
        Require(cards.Select(card => frames.World.ReadSparse<Card>(card).Title).SequenceEqual(["2 old", "5 old"]),
            "Static content delegates preserve equal row content when the parent changes.");
        frames.World.Replace(root, new ContentPanel(7, 2));
        frames.Frame();
        Require(content.SequenceEqual(Hierarchy<ElementTree>.GetChildren(frames.World, rows)) &&
            cards.Select(card => frames.World.ReadSparse<Card>(card).Title).SequenceEqual(["7 new", "5 old"]),
            "Changed content arguments refresh the corresponding row.");
    }

    [Composable]
    private static void Rows<T>(IReadOnlyList<T> items, int tick, [Composable] Action<T> row)
    {
        foreach (T item in items)
            row(item);
    }

    private readonly record struct ContentPanel(int First, int Tick) : IView, IComponent
    {
        public BuildTask Build()
        {
            Rows(new[] { First, 5 }, Tick, static item => Scope.View(new Card(item + s_decoration)));
            return default;
        }
    }

    [Composable]
    private static async BuildTask Label(string text)
    {
        await default(BuildTask);
        Content(text + s_decoration);
    }

    [Composable]
    private static void Content(string title) => Scope.View(new Card(title));

    private readonly record struct Panel(string Text, int Tick) : IView, IComponent
    {
        public BuildTask Build()
        {
            _ = Label(Text);
            return default;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
