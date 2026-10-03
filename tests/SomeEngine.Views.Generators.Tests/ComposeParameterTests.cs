using SomeEngine.Views.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ComposeParameterTests
{
    [Fact]
    public async Task PrivateRecordConstraintsCanBeUsedByTheContainingTypesGeneratedViews()
    {
        bool compared = await CompilerChecks.Run("""
            using SomeEngine.Views.Compose;
            public static partial class ReferenceChecks
            {
                private record Value(int Number);
                [Composable] private static void Label<T>(T value) where T : Value { }

                public static bool Check() => new Label_View<Value>(new(3)).Equals(new Label_View<Value>(new(3)));
            }
            """);

        Assert.True(compared);
    }

    [Fact]
    public void GenericRecordParametersUseTheirRecordsValueEquality()
    {
        var first = new ComparisonScreens.Record_View<ScoreRecord>(new(3));
        var equal = new ComparisonScreens.Record_View<ScoreRecord>(new(3));

        Assert.Equal(first, equal);
        Assert.Contains(equal, new HashSet<ComparisonScreens.Record_View<ScoreRecord>> { first });
        Assert.NotEqual(first, new ComparisonScreens.Record_View<ScoreRecord>(new(7)));
    }

    [Fact]
    public async Task ComposableContentUsesDelegateReferenceWhileCallbacksKeepEqualViewsInterchangeable()
    {
        bool compared = await CompilerChecks.Run("""
            using SomeEngine.Views.Compose;
            using System;
            using System.Collections.Generic;

            public static class ReferenceChecks
            {
                public static bool Check()
                {
                    Action content = new Action(Display);
                    Action replacement = new Action(Display);
                    var first = new Composition.Host_View(content, static () => { });
                    var equal = new Composition.Host_View(content, static () => { });
                    var changed = new Composition.Host_View(replacement, first.changed);
                    return content.Equals(replacement) && first.Equals(equal) &&
                        new HashSet<Composition.Host_View> { first }.Contains(equal) &&
                        !first.Equals(changed) &&
                        new Composition.Host_View(null, first.changed).Equals(new Composition.Host_View(null, equal.changed));
                }

                public static void Display() { }
            }

            public static partial class Composition
            {
                [Composable]
                public static void Host([Composable] Action? content, Action changed) { }
            }
            """);

        Assert.True(compared);
    }

    [Fact]
    public void ClassParametersCompareByReferenceWhileValuesRecordsAndStringsCompareByValue()
    {
        var score = new Score(3);
        var first = new ComparisonScreens.Label_View(new(5), new(7), "text", score);
        var equal = new ComparisonScreens.Label_View(new(5), new(7), new string("text".ToCharArray()), score);
        var anotherReference = new ComparisonScreens.Label_View(new(5), new(7), "text", new Score(3));

        Assert.Equal(first, equal);
        Assert.Contains(equal, new HashSet<ComparisonScreens.Label_View> { first });
        Assert.NotEqual(first, anotherReference);
        Assert.NotEqual(first, equal with { value = new(11) });
        Assert.NotEqual(first, equal with { record = new(13) });
        Assert.NotEqual(first, equal with { text = "changed" });
        Assert.Equal(new ComparisonScreens.Label_View(default, null, null, null),
            new ComparisonScreens.Label_View(default, null, null, null));
    }
}

public readonly record struct ScoreValue(int Value);
public record ScoreRecord(int Value);

public sealed class Score(int value)
{
    public int Value { get; } = value;
    public override bool Equals(object? other) => other is Score score && score.Value == Value;
    public override int GetHashCode() => Value;
}

public static partial class ComparisonScreens
{
    [Composable]
    public static void Record<T>(T record) where T : ScoreRecord { }

    [Composable]
    public static void Label(ScoreValue value, ScoreRecord? record, string? text, Score? score) { }
}
