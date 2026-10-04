using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed class TextFieldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerReceivesEditsAndPassesTheTextBackWithoutMovingTheCaret(bool rewritten) =>
        TextFieldChecks.RunFeedback(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedCaretAndSelectionFollowTheDisplayedGlyphs(bool rewritten) => TextFieldChecks.RunSelection(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyTextShowsThePlaceholderWhileTheCaretStaysAtTheTextStart(bool rewritten) => TextFieldChecks.RunPlaceholder(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionReplacesTheSelectionsDisplayAndCommitsThroughEdited(bool rewritten) => TextFieldChecks.RunComposition(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterAndCompositionInputKeepOneLineAtItsNaturalWidth(bool rewritten) => TextFieldChecks.RunSingleLine(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerParametersReplaceTextAndRetainEditsDuringRepeatedDeclarations(bool rewritten) => TextFieldChecks.RunParameters(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThemeChangesUpdateTextAndEditingGeometryTogether(bool rewritten) => TextFieldChecks.RunTheme(rewritten);

#if DEBUG
    [Theory]
    [InlineData(false, null, "")]
    [InlineData(true, null, "")]
    [InlineData(false, "A\nB", "")]
    [InlineData(true, "A\nB", "")]
    [InlineData(false, "", null)]
    [InlineData(true, "", null)]
    [InlineData(false, "", "A\rB")]
    [InlineData(true, "", "A\rB")]
    public void InvalidParametersReportTheSingleLineRequirement(bool rewritten, string? text, string? placeholder)
    {
        using var frames = new ValueFrames(text: true);
        Entity root = rewritten
            ? Views.Views.Spawn(frames.World, frames.Canvas, new Rewrites.TextField(text!, placeholder!))
            : Views.Views.Spawn(frames.World, frames.Canvas, new TextField(text!, placeholder!));

        frames.Frame();

        Assert.True(frames.World.HasSparse<BuildError>(root));
        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Contains("single-line string", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextFieldsRequireAnActiveBuildScope(bool rewritten)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (rewritten)
                new Rewrites.TextField("").Build();
            else
                new TextField("").Build();
        });
        Assert.Contains("active view build", error.Message);
    }
#endif
}
