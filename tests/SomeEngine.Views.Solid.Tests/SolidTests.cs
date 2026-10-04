using SomeEngine.Core.Math;
using System.Globalization;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
using SomeEngine.Typography;
using SomeEngine.Views.Solid;
using static SomeEngine.Views.Solid.Solid;
using SolidFrames = SomeEngine.Views.Tests.HeadlessFrameDriver;

namespace SomeEngine.Views.Solid.Tests;

public sealed partial class SolidTests
{
    [Fact]
    public void ManagedAndNativeConsumersShareTheSolidBehavior() => SolidChecks.Run();

    [Fact]
    public void StateChangesUpdateTheExistingTextAndPreserveOtherFieldsAndComponentContent()
    {
        using var frames = new SolidFrames();
        var count = new State<string>("three");
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(Counter));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Entity label = children[0];
        Entity sibling = children[1];
        uint textVersion = frames.World.WriteVersion<Elements.Text>(label);
        uint styleVersion = frames.World.WriteVersion<TextStyle>(label);
        uint surfaceVersion = frames.World.WriteVersion<Surface>(label);
        frames.World.Replace(root, new Display("edited component"));
        sibling.SetText(frames.World, "edited sibling");

        count.Value = "seven";
        count.Value = "eleven";
        frames.Frame();

        Assert.Equal(children, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        Assert.Equal("eleven", frames.World.Read<Elements.Text>(label).Value);
        Assert.NotEqual(textVersion, frames.World.WriteVersion<Elements.Text>(label));
        Assert.Equal(styleVersion, frames.World.WriteVersion<TextStyle>(label));
        Assert.Equal(surfaceVersion, frames.World.WriteVersion<Surface>(label));
        Assert.Equal("edited component", frames.World.Read<Display>(root).Value);
        Assert.Equal("edited sibling", frames.World.Read<Elements.Text>(sibling).Value);

        void Counter()
        {
            Scope.Write<Display>(Scope.Entity).Value = "created component";
            Text(() => count.Value).FontSize(20).Background(new Color(1, 0, 0));
            Text("sibling");
        }
    }

    [Fact]
    public void EqualMemoResultsPreserveEditedTextAndChangedResultsRefreshOnRead()
    {
        using var frames = new SolidFrames();
        var count = new State<int>(3);
        Func<bool> positive = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            positive = Memo(() => count.Value > 0);
            Text(() => positive() ? "positive" : "negative");
        }));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        label.SetText(frames.World, "edited");
        uint version = frames.World.WriteVersion<Elements.Text>(label);

        count.Value = 7;
        Assert.True(positive());
        frames.Frame();

        Assert.Equal("edited", frames.World.Read<Elements.Text>(label).Value);
        Assert.Equal(version, frames.World.WriteVersion<Elements.Text>(label));

        count.Value = -1;
        Assert.False(positive());
        frames.Frame();

        Assert.Equal("negative", frames.World.Read<Elements.Text>(label).Value);
        Assert.NotEqual(version, frames.World.WriteVersion<Elements.Text>(label));
    }

    [Fact]
    public void EffectsMergeFrameChangesAndSampleUntrackedStateWhenTheirTrackedStateChanges()
    {
        using var frames = new SolidFrames();
        var sample = new State<int>(3);
        var trigger = new State<int>(5);
        Func<int> readSample = () => sample.Value;
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Effect(() => Scope.Write<Total>(Scope.Entity).Value = Untrack(readSample) + trigger.Value)));
        frames.Frame();
        Assert.Equal(8, frames.World.Read<Total>(root).Value);

        sample.Value = 7;
        Assert.Equal(7, sample.Value);
        frames.Frame();
        Assert.Equal(8, frames.World.Read<Total>(root).Value);

        trigger.Value = 9;
        trigger.Value = 11;
        Assert.Equal(8, frames.World.Read<Total>(root).Value);
        frames.Frame();

        Assert.Equal(18, frames.World.Read<Total>(root).Value);
    }

    [Fact]
    public void AReactiveBackgroundComposesWithOtherSurfaceFieldsAndPreservesEqualVersions()
    {
        using var frames = new SolidFrames();
        var fill = new State<Color>(new Color(1, 0, 0));
        var border = new Color(0, 0, 1);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Text("label").Corner(4).Background(() => fill.Value).Border(border, 2)));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(fill.Value, frames.World.Read<Surface>(label).Fill);
        Assert.Equal(4, frames.World.Read<Surface>(label).CornerRadius);
        Assert.Equal(border, frames.World.Read<Surface>(label).BorderColor);
        Assert.Equal(2, frames.World.Read<Surface>(label).BorderWidth);
        frames.World.Replace(label, new Surface { Fill = fill.Value, CornerRadius = 8, BorderColor = border, BorderWidth = 3 });
        uint textVersion = frames.World.WriteVersion<Elements.Text>(label);

        fill.Value = new Color(0, 1, 0);
        frames.Frame();

        Assert.Equal(fill.Value, frames.World.Read<Surface>(label).Fill);
        Assert.Equal(8, frames.World.Read<Surface>(label).CornerRadius);
        Assert.Equal(border, frames.World.Read<Surface>(label).BorderColor);
        Assert.Equal(3, frames.World.Read<Surface>(label).BorderWidth);
        Assert.Equal(textVersion, frames.World.WriteVersion<Elements.Text>(label));
        uint surfaceVersion = frames.World.WriteVersion<Surface>(label);
        fill.Value = new Color(0, 1, 0);
        frames.Frame();
        Assert.Equal(surfaceVersion, frames.World.WriteVersion<Surface>(label));
    }

    [Fact]
    public void ReactiveNumbersUseTheirFormatsAndPreserveEqualTextVersions()
    {
        using var frames = new SolidFrames();
        var count = new State<int>(42);
        var amount = new State<double>(12.341);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            Text(42, "D4");
            Text(() => count.Value, "D4");
            Text(() => amount.Value, "F2", CultureInfo.GetCultureInfo("fr-FR"));
            Text(42);
            Text(() => count.Value);
        }));
        frames.Frame();
        Entity[] labels = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("0042", frames.World.Read<Elements.Text>(labels[0]).Value);
        Assert.Equal("0042", frames.World.Read<Elements.Text>(labels[1]).Value);
        Assert.Equal("12,34", frames.World.Read<Elements.Text>(labels[2]).Value);
        Assert.Equal("42", frames.World.Read<Elements.Text>(labels[3]).Value);
        Assert.Equal("42", frames.World.Read<Elements.Text>(labels[4]).Value);
        uint version = frames.World.WriteVersion<Elements.Text>(labels[2]);
        string text = frames.World.Read<Elements.Text>(labels[2]).Value;

        count.Value = 43;
        amount.Value = 12.342;
        frames.Frame();

        Assert.Equal("0043", frames.World.Read<Elements.Text>(labels[1]).Value);
        Assert.Equal("43", frames.World.Read<Elements.Text>(labels[4]).Value);
        Assert.Same(text, frames.World.Read<Elements.Text>(labels[2]).Value);
        Assert.Equal(version, frames.World.WriteVersion<Elements.Text>(labels[2]));
    }

    [Fact]
    public void StateChangesWithEqualFormattedNumbersAllocateZeroBytesAfterWarmup()
    {
        using var frames = new SolidFrames();
        var count = new State<int>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            Text(() => count.Value / 10000, "D4");
            Func<int> total = Memo(() => count.Value / 10000);
            Text(total, "D4");
        }));
        frames.Frame();
        Entity[] labels = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        for (int index = 0; index < 1000; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        string text = frames.World.Read<Elements.Text>(labels[0]).Value;
        uint version = frames.World.WriteVersion<Elements.Text>(labels[0]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4096; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

#if !DEBUG
        Assert.Equal(0, allocated);
#endif
        Assert.Same(text, frames.World.Read<Elements.Text>(labels[0]).Value);
        Assert.Equal(version, frames.World.WriteVersion<Elements.Text>(labels[0]));
        Assert.Equal(labels, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        count.Value = 10000;
        frames.Frame();
        Assert.Equal("0001", frames.World.Read<Elements.Text>(labels[0]).Value);
        Assert.Equal("0001", frames.World.Read<Elements.Text>(labels[1]).Value);
    }

    [Fact]
    public void ChangedNumericTextAllocatesOnlyTheFormattedStringsAfterWarmup()
    {
        using var frames = new SolidFrames();
        var count = new State<int>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            Func<int> number = Memo(() => count.Value);
            Text(number, "D4", CultureInfo.InvariantCulture);
        }));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        for (int index = 0; index < 1000; index++)
        {
            count.Value = index;
            frames.Frame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4096; index++)
            label.SetText(frames.World, index, "D4", CultureInfo.InvariantCulture);
        long formattingBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4096; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        long frameBytes = GC.GetAllocatedBytesForCurrentThread() - before;

#if !DEBUG
        Assert.Equal(formattingBytes, frameBytes);
#endif
        Assert.Equal("4095", frames.World.Read<Elements.Text>(label).Value);
        Assert.Equal(label, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
    }

    [Fact]
    public void ReactiveTextStyleFieldsFollowTheirGettersAndPreserveEachOthersValues()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf"));
        using Font first = Font.Load(bytes);
        using Font second = Font.Load(bytes);
        using var frames = new SolidFrames();
        var font = new State<Font>(first);
        var size = new State<float>(20);
        var color = new State<Color>(new Color(1, 0, 0));
        var height = new State<float>(24);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Text("label").Font(() => font.Value).FontSize(() => size.Value)
                .TextColor(() => color.Value).LineHeight(() => height.Value)));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        TextStyle style = frames.World.Read<TextStyle>(label);
        Assert.Same(first, style.Font);
        Assert.Equal(20, style.Size);
        Assert.Equal(color.Value, style.Color);
        Assert.Equal(24, style.LineHeight);
        style.Size = 33;
        frames.World.Replace(label, style);
        uint textVersion = frames.World.WriteVersion<Elements.Text>(label);

        font.Value = second;
        color.Value = new Color(0, 1, 0);
        height.Value = 48;
        frames.Frame();

        style = frames.World.Read<TextStyle>(label);
        Assert.Same(second, style.Font);
        Assert.Equal(33, style.Size);
        Assert.Equal(color.Value, style.Color);
        Assert.Equal(48, style.LineHeight);
        Assert.Equal(textVersion, frames.World.WriteVersion<Elements.Text>(label));

        size.Value = 32;
        frames.Frame();
        Assert.Equal(32, frames.World.Read<TextStyle>(label).Size);
        Assert.Equal(color.Value, frames.World.Read<TextStyle>(label).Color);
        Assert.Equal(48, frames.World.Read<TextStyle>(label).LineHeight);
    }

    [Fact]
    public void ReactiveCornersAndBordersPreserveTheCurrentBackground()
    {
        using var frames = new SolidFrames();
        var radius = new State<float>(4);
        var color = new State<Color>(new Color(0, 0, 1));
        var width = new State<float>(2);
        var fill = new Color(1, 0, 0);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Text("label").Background(fill).Corner(() => radius.Value).Border(() => color.Value, () => width.Value)));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(fill, frames.World.Read<Surface>(label).Fill);
        Assert.Equal(4, frames.World.Read<Surface>(label).CornerRadius);
        Assert.Equal(color.Value, frames.World.Read<Surface>(label).BorderColor);
        Assert.Equal(2, frames.World.Read<Surface>(label).BorderWidth);
        frames.World.Replace(label, new Surface { Fill = new Color(0, 1, 0), CornerRadius = 4,
            BorderColor = color.Value, BorderWidth = 2 });

        radius.Value = 8;
        color.Value = new Color(1, 1, 0);
        width.Value = 3;
        frames.Frame();

        Assert.Equal(new Color(0, 1, 0), frames.World.Read<Surface>(label).Fill);
        Assert.Equal(8, frames.World.Read<Surface>(label).CornerRadius);
        Assert.Equal(color.Value, frames.World.Read<Surface>(label).BorderColor);
        Assert.Equal(3, frames.World.Read<Surface>(label).BorderWidth);
    }

    [Fact]
    public void ChangedBackgroundFieldsAllocateZeroBytesAfterWarmup()
    {
        using var frames = new SolidFrames();
        var count = new State<int>();
        var red = new Color(1, 0, 0);
        var green = new Color(0, 1, 0);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Text("label").Corner(4).Background(() => count.Value % 2 == 0 ? red : green)));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        for (int index = 0; index < 1000; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        uint textVersion = frames.World.WriteVersion<Elements.Text>(label);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4096; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

#if !DEBUG
        Assert.Equal(0, allocated);
#endif
        Assert.Equal(green, frames.World.Read<Surface>(label).Fill);
        Assert.Equal(4, frames.World.Read<Surface>(label).CornerRadius);
        Assert.Equal(textVersion, frames.World.WriteVersion<Elements.Text>(label));
    }

    [Fact]
    public void FailedTextGettersPreserveTheirValueAndRecoverWhenTheirSourceChanges()
    {
        using var frames = new SolidFrames();
        var count = new State<string>("initial");
        var fail = new State<bool>();
        var error = new InvalidOperationException("text getter failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() => Text(() =>
        {
            if (fail.Value)
                throw error;
            return count.Value;
        })));
        frames.Frame();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint version = frames.World.WriteVersion<Elements.Text>(label);

        fail.Value = true;
        count.Value = "latest";
        frames.Frame();

        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal("initial", frames.World.Read<Elements.Text>(label).Value);
        Assert.Equal(version, frames.World.WriteVersion<Elements.Text>(label));

        fail.Value = false;
        frames.Frame();

        Assert.Equal("latest", frames.World.Read<Elements.Text>(label).Value);
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

#if DEBUG
    [Fact]
    public void SolidCallsReportTheirRequiredScopeAndGetters()
    {
        Assert.Throws<InvalidOperationException>(() => Effect(() => { }));
        Assert.Throws<InvalidOperationException>(() => Insert(() => { }));
        Assert.Throws<InvalidOperationException>(() => Memo(() => 3));
        Assert.Throws<InvalidOperationException>(() => Untrack(() => 3));
        Assert.Throws<InvalidOperationException>(() => Effect(null!));
        Assert.Throws<InvalidOperationException>(() => Insert(null!));
        Assert.Throws<InvalidOperationException>(() => Memo((Func<int>)null!));
        Assert.Throws<InvalidOperationException>(() => Untrack((Func<int>)null!));
        Assert.Throws<InvalidOperationException>(() => Text((Func<string>)null!));
        Assert.Throws<InvalidOperationException>(() => Text((Func<int>)null!));
        Assert.Throws<InvalidOperationException>(() => Entity.Null.Background((Func<Color>)null!));
    }
#endif

    private record struct Display(string Value) : IComponent;
    private record struct Total(int Value) : IComponent;

    private readonly record struct OnceView(Action Content) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Content();
            return default;
        }
    }

}
