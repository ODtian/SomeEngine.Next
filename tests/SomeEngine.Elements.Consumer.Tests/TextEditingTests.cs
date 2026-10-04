namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class TextEditingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedNodeInsertsTextAndConsumesEachEditOnce(bool rewritten) => TextEditingChecks.RunInsertion(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaretMovesAcrossCompleteTextElements(bool rewritten) => TextEditingChecks.RunMovement(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftSelectionReplacesTextInEitherDirection(bool rewritten) => TextEditingChecks.RunSelection(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletionRemovesCompleteCharactersAndSelections(bool rewritten) => TextEditingChecks.RunDeletion(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionDisplaysCandidatesAndCommitsThroughTextInput(bool rewritten) => TextEditingChecks.RunComposition(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionKeepsEditingKeysForTheInputMethod(bool rewritten) => TextEditingChecks.RunCompositionKeys(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedInputEditsEachFocusedNodeAndEndsItsComposition(bool rewritten) => TextEditingChecks.RunInputOrder(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerticalMovementUsesTheCurrentTextElementColumn(bool rewritten) => TextEditingChecks.RunVerticalMovement(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertionsKeepTheCaretAtACompleteTextElementBoundary(bool rewritten) => TextEditingChecks.RunJoinedCharacters(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyInputPreservesTheSelectionAndComposition(bool rewritten) => TextEditingChecks.RunEmptyInput(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InactiveEditorsPreserveTheirTextThroughKeyboardInput(bool rewritten) => TextEditingChecks.RunInactive(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedDeclarationsPreserveTheEditingDisplay(bool rewritten) => TextEditingChecks.RunRepeatedDeclaration(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldsMaintainTheirOwnTextSelectionAndComposition(bool rewritten) => TextEditingChecks.RunWorlds(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusabilityRemovalEndsCompositionAndRetainsText(bool rewritten) => TextEditingChecks.RunFocusabilityRemoval(rewritten);
}
